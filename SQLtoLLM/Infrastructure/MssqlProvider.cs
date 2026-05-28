using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using SQLtoLLM.Core;
using SQLtoLLM.Core.Models;

namespace SQLtoLLM.Infrastructure;

public static class MssqlProvider
{
    // ──────────────────────────────────────────────────────────────────────────
    //  Connection
    // ──────────────────────────────────────────────────────────────────────────

    public static async Task TestConnectionAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
    }

    public static async Task<bool> CheckViewDefinitionPermissionAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        const string sql = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')";
        await using var cmd = new SqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync();
        return result is int i && i == 1;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Resolve: detect type from catalog
    // ──────────────────────────────────────────────────────────────────────────

    public static async Task<List<DbObject>> ResolveObjectsAsync(
        IEnumerable<string> names,
        string connectionString)
    {
        var results = new List<DbObject>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        foreach (var rawName in names)
        {
            var name = rawName.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var dbObj = new DbObject { ObjectName = name };

            // 1. Try sys.objects (TABLE, VIEW, PROCEDURE, TRIGGER)
            const string sqlObjects = """
                SELECT
                    SCHEMA_NAME(o.schema_id) + '.' + o.name COLLATE DATABASE_DEFAULT AS FullName,
                    CASE o.type
                        WHEN 'U'  THEN 'TABLE'
                        WHEN 'V'  THEN 'VIEW'
                        WHEN 'P'  THEN 'PROCEDURE'
                        WHEN 'TR' THEN 'TRIGGER'
                        ELSE o.type_desc COLLATE DATABASE_DEFAULT
                    END AS ObjectType
                FROM sys.objects o
                WHERE SCHEMA_NAME(o.schema_id) + '.' + o.name COLLATE DATABASE_DEFAULT = @Name
                  AND o.type IN ('U','V','P','TR')
                """;

            await using var cmd = new SqlCommand(sqlObjects, conn);
            cmd.Parameters.AddWithValue("@Name", name);

            var matches = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    matches.Add(reader.GetString(1));
            }

            if (matches.Count == 1)
            {
                var typeStr = matches[0];
                dbObj.DetectedType = ParseType(typeStr);
                dbObj.EditableType = dbObj.DetectedType;
                dbObj.Status = ObjectStatus.Resolved;
            }
            else if (matches.Count > 1)
            {
                dbObj.Status = ObjectStatus.Ambiguous;
            }
            else
            {
                // 2. Try sys.indexes (INDEX — no schema prefix in sys.indexes)
                const string sqlIndex = """
                    SELECT TOP 1 i.name COLLATE DATABASE_DEFAULT
                    FROM sys.indexes i
                    JOIN sys.tables t ON t.object_id = i.object_id
                    WHERE i.name COLLATE DATABASE_DEFAULT = PARSENAME(@Name, 1)
                      AND i.is_hypothetical = 0
                      AND i.type > 0
                    """;

                await using var cmdIdx = new SqlCommand(sqlIndex, conn);
                cmdIdx.Parameters.AddWithValue("@Name", name);

                var found = await cmdIdx.ExecuteScalarAsync();
                if (found is not null)
                {
                    dbObj.DetectedType = ObjectType.Index;
                    dbObj.EditableType = ObjectType.Index;
                    dbObj.Status = ObjectStatus.Resolved;
                }
                else
                {
                    dbObj.Status = ObjectStatus.NotFound;
                }
            }

            results.Add(dbObj);
        }

        return results;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Extract: run the extraction query and return formatted text
    // ──────────────────────────────────────────────────────────────────────────

    public static async Task<string> ExtractContextAsync(
        IEnumerable<DbObject> objects,
        string connectionString)
    {
        var items = objects.ToList();
        if (items.Count == 0) return string.Empty;

        // Build the @Objects INSERT values
        var insertLines = new StringBuilder();
        foreach (var obj in items)
        {
            var typeName = TypeToSqlString(obj.EditableType!.Value);
            var safeName = obj.ObjectName.Replace("'", "''");
            insertLines.AppendLine($"INSERT INTO @Objects VALUES ('{safeName}', '{typeName}');");
        }

        var sql = BuildExtractionSql(insertLines.ToString());

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // 1. Fetch Server Properties
        var propertiesText = new StringBuilder();
        const string sqlProperties = """
            SELECT 
                CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(MAX)) AS ProductVersion,
                CAST(SERVERPROPERTY('ProductLevel') AS NVARCHAR(MAX)) AS ProductLevel,
                CAST(SERVERPROPERTY('ProductUpdateLevel') AS NVARCHAR(MAX)) AS ProductUpdateLevel,
                CAST(SERVERPROPERTY('ProductUpdateReference') AS NVARCHAR(MAX)) AS KBArticle,
                CAST(SERVERPROPERTY('Edition') AS NVARCHAR(MAX)) AS Edition,
                CAST(SERVERPROPERTY('EditionID') AS NVARCHAR(MAX)) AS EditionID,
                CAST(SERVERPROPERTY('EngineEdition') AS NVARCHAR(MAX)) AS EngineEdition,
                CAST(@@VERSION AS NVARCHAR(MAX)) AS FullVersionString;
            """;

        await using (var cmdProp = new SqlCommand(sqlProperties, conn))
        {
            await using (var readerProp = await cmdProp.ExecuteReaderAsync())
            {
                if (await readerProp.ReadAsync())
                {
                    var productVersion = await readerProp.IsDBNullAsync(0) ? "N/A" : readerProp.GetString(0);
                    var productLevel = await readerProp.IsDBNullAsync(1) ? "N/A" : readerProp.GetString(1);
                    var productUpdateLevel = await readerProp.IsDBNullAsync(2) ? "N/A" : readerProp.GetString(2);
                    var kbArticle = await readerProp.IsDBNullAsync(3) ? "N/A" : readerProp.GetString(3);
                    var edition = await readerProp.IsDBNullAsync(4) ? "N/A" : readerProp.GetString(4);
                    var editionId = await readerProp.IsDBNullAsync(5) ? "N/A" : readerProp.GetString(5);
                    var engineEdition = await readerProp.IsDBNullAsync(6) ? "N/A" : readerProp.GetString(6);
                    var fullVersionString = await readerProp.IsDBNullAsync(7) ? "N/A" : readerProp.GetString(7);

                    propertiesText.AppendLine("================================================================================");
                    propertiesText.AppendLine("DATABASE SERVER INFORMATION");
                    propertiesText.AppendLine("================================================================================");
                    propertiesText.AppendLine("CONTEXT:");
                    propertiesText.AppendLine($"ProductVersion       : {productVersion}");
                    propertiesText.AppendLine($"ProductLevel         : {productLevel}");
                    propertiesText.AppendLine($"ProductUpdateLevel   : {productUpdateLevel}");
                    propertiesText.AppendLine($"KBArticle            : {kbArticle}");
                    propertiesText.AppendLine($"Edition              : {edition}");
                    propertiesText.AppendLine($"EditionID            : {editionId}");
                    propertiesText.AppendLine($"EngineEdition        : {engineEdition}");
                    propertiesText.AppendLine($"FullVersionString    : {fullVersionString.Trim()}");
                    propertiesText.AppendLine("================================================================================");
                    propertiesText.AppendLine();
                    propertiesText.AppendLine();
                }
            }
        }

        // 2. Fetch Objects Context
        var rows = new List<(string ObjectType, string ObjectName, string ContextText)>();

        await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
        {
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var objectType = await reader.IsDBNullAsync(0) ? string.Empty : reader.GetString(0);
                    var objectName = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);
                    var contextText = await reader.IsDBNullAsync(2) ? string.Empty : reader.GetString(2);
                    rows.Add((objectType, objectName, contextText));
                }
            }
        }

        var objectsText = OutputFormatter.Format(rows);
        return propertiesText.ToString() + objectsText;
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static ObjectType? ParseType(string s) => s.ToUpperInvariant() switch
    {
        "TABLE" => ObjectType.Table,
        "VIEW" => ObjectType.View,
        "PROCEDURE" => ObjectType.Procedure,
        "INDEX" => ObjectType.Index,
        "TRIGGER" => ObjectType.Trigger,
        _ => null
    };

    private static string TypeToSqlString(ObjectType t) => t switch
    {
        ObjectType.Table => "TABLE",
        ObjectType.View => "VIEW",
        ObjectType.Procedure => "PROCEDURE",
        ObjectType.Index => "INDEX",
        ObjectType.Trigger => "TRIGGER",
        _ => "TABLE"
    };

    private static string BuildExtractionSql(string insertStatements) => $"""
        DECLARE @Objects TABLE
        (
            ObjectName SYSNAME COLLATE DATABASE_DEFAULT,
            ObjectType VARCHAR(20) COLLATE DATABASE_DEFAULT
        );

        {insertStatements}

        SELECT
            o.ObjectType,
            o.ObjectName,
            ContextText =
            (
                CASE o.ObjectType

                /* ======================= TABLE ======================= */
                WHEN 'TABLE' THEN
                    'CREATE TABLE ' + o.ObjectName + ' (' + CHAR(10) +

                    STUFF((
                        SELECT
                            ',' + CHAR(10) +
                            '    ' + c.name COLLATE DATABASE_DEFAULT + ' ' +
                            t.name COLLATE DATABASE_DEFAULT +
                            CASE
                                WHEN t.name IN ('varchar','char','nvarchar','nchar')
                                    THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX'
                                                    ELSE CAST(
                                                             CASE WHEN t.name IN ('nvarchar','nchar')
                                                                  THEN c.max_length / 2
                                                                  ELSE c.max_length
                                                             END AS VARCHAR)
                                                    END + ')'
                                WHEN t.name IN ('decimal','numeric')
                                    THEN '(' + CAST(c.precision AS VARCHAR) + ',' + CAST(c.scale AS VARCHAR) + ')'
                                ELSE ''
                            END +
                            CASE WHEN c.is_identity = 1 THEN ' IDENTITY(1,1)' ELSE '' END +
                            CASE WHEN c.is_nullable = 0 THEN ' NOT NULL' ELSE ' NULL' END +
                            ISNULL(' DEFAULT ' + dc.definition COLLATE DATABASE_DEFAULT, '')
                        FROM sys.columns c
                        JOIN sys.types t ON t.user_type_id = c.user_type_id
                        LEFT JOIN sys.default_constraints dc
                            ON dc.parent_object_id = c.object_id
                           AND dc.parent_column_id = c.column_id
                        WHERE c.object_id = obj.object_id
                        ORDER BY c.column_id
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '')

                    + ISNULL(
                        (
                            SELECT
                                ',' + CHAR(10) +
                                '    CONSTRAINT ' + kc.name COLLATE DATABASE_DEFAULT +
                                ' PRIMARY KEY (' +
                                STUFF((
                                    SELECT ', ' + c2.name COLLATE DATABASE_DEFAULT
                                    FROM sys.index_columns ic
                                    JOIN sys.columns c2
                                      ON c2.object_id = ic.object_id
                                     AND c2.column_id = ic.column_id
                                    WHERE ic.object_id = kc.parent_object_id
                                      AND ic.index_id = kc.unique_index_id
                                    ORDER BY ic.key_ordinal
                                    FOR XML PATH(''), TYPE
                                ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                                ')'
                            FROM sys.key_constraints kc
                            WHERE kc.parent_object_id = obj.object_id
                              AND kc.type = 'PK'
                        ),
                        ''
                    )
                    + CHAR(10) + ');' + CHAR(10) +

                    /* -------- INDEXES -------- */
                    ISNULL(
                    (
                        STUFF((
                            SELECT
                                CHAR(10) +
                                'CREATE ' +
                                CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END +
                                i.type_desc COLLATE DATABASE_DEFAULT +
                                ' INDEX ' + i.name COLLATE DATABASE_DEFAULT +
                                ' ON ' + o.ObjectName +
                                ' (' +
                                STUFF((
                                    SELECT ', ' + c.name COLLATE DATABASE_DEFAULT
                                    FROM sys.index_columns ic2
                                    JOIN sys.columns c
                                      ON c.object_id = ic2.object_id
                                     AND c.column_id = ic2.column_id
                                    WHERE ic2.object_id = i.object_id
                                      AND ic2.index_id = i.index_id
                                      AND ic2.key_ordinal > 0
                                    ORDER BY ic2.key_ordinal
                                    FOR XML PATH(''), TYPE
                                ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                                ');'
                            FROM sys.indexes i
                            WHERE i.object_id = obj.object_id
                              AND i.is_primary_key = 0
                              AND i.is_hypothetical = 0
                              AND i.type > 0
                            ORDER BY i.name
                            FOR XML PATH(''), TYPE
                        ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 1, '')
                    ), '') +

                    /* -------- FOREIGN KEYS -------- */
                    ISNULL(
                    (
                        STUFF((
                            SELECT
                                CHAR(10) +
                                'ALTER TABLE ' + o.ObjectName +
                                ' ADD CONSTRAINT ' + fk.name COLLATE DATABASE_DEFAULT +
                                ' FOREIGN KEY (' +
                                STUFF((
                                    SELECT ', ' + pc.name COLLATE DATABASE_DEFAULT
                                    FROM sys.foreign_key_columns fkc
                                    JOIN sys.columns pc
                                      ON pc.object_id = fkc.parent_object_id
                                     AND pc.column_id = fkc.parent_column_id
                                    WHERE fkc.constraint_object_id = fk.object_id
                                    ORDER BY fkc.constraint_column_id
                                    FOR XML PATH(''), TYPE
                                ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                                ') REFERENCES ' +
                                SCHEMA_NAME(rt.schema_id) + '.' +
                                rt.name COLLATE DATABASE_DEFAULT +
                                ' (' +
                                STUFF((
                                    SELECT ', ' + rc.name COLLATE DATABASE_DEFAULT
                                    FROM sys.foreign_key_columns fkc
                                    JOIN sys.columns rc
                                      ON rc.object_id = fkc.referenced_object_id
                                     AND rc.column_id = fkc.referenced_column_id
                                    WHERE fkc.constraint_object_id = fk.object_id
                                    ORDER BY fkc.constraint_column_id
                                    FOR XML PATH(''), TYPE
                                ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                                ');'
                            FROM sys.foreign_keys fk
                            JOIN sys.tables rt
                                ON rt.object_id = fk.referenced_object_id
                            WHERE fk.parent_object_id = obj.object_id
                            ORDER BY fk.name
                            FOR XML PATH(''), TYPE
                        ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 1, '')
                    ), '')

                /* ======================= VIEW ======================= */
                WHEN 'VIEW' THEN
                    ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<No VIEW DEFINITION>>')

                /* ======================= PROCEDURE ======================= */
                WHEN 'PROCEDURE' THEN
                    ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<No PROCEDURE DEFINITION>>')

                /* ======================= INDEX ======================= */
                WHEN 'INDEX' THEN
                    (
                        SELECT TOP 1
                            'CREATE ' +
                            CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END +
                            i.type_desc COLLATE DATABASE_DEFAULT +
                            ' INDEX ' + i.name COLLATE DATABASE_DEFAULT +
                            ' ON ' + s.name COLLATE DATABASE_DEFAULT + '.' +
                            tb.name COLLATE DATABASE_DEFAULT +
                            ' (' +
                            STUFF((
                                SELECT ', ' + c.name COLLATE DATABASE_DEFAULT
                                FROM sys.index_columns ic2
                                JOIN sys.columns c
                                  ON c.object_id = ic2.object_id
                                 AND c.column_id = ic2.column_id
                                WHERE ic2.object_id = i.object_id
                                  AND ic2.index_id = i.index_id
                                  AND ic2.key_ordinal > 0
                                ORDER BY ic2.key_ordinal
                                FOR XML PATH(''), TYPE
                            ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                            ');'
                        FROM sys.indexes i
                        JOIN sys.tables tb ON tb.object_id = i.object_id
                        JOIN sys.schemas s ON s.schema_id = tb.schema_id
                        WHERE i.name COLLATE DATABASE_DEFAULT = PARSENAME(o.ObjectName, 1)
                          AND i.is_hypothetical = 0
                          AND i.type > 0
                    )

                /* ======================= TRIGGER ======================= */
                WHEN 'TRIGGER' THEN
                    ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<No TRIGGER DEFINITION>>')

                END
            ) COLLATE DATABASE_DEFAULT
        FROM @Objects o
        LEFT JOIN sys.objects obj
          ON CONCAT(SCHEMA_NAME(obj.schema_id), '.', obj.name) COLLATE DATABASE_DEFAULT
             = o.ObjectName
        LEFT JOIN sys.sql_modules m
          ON m.object_id = obj.object_id
        ORDER BY
            CASE o.ObjectType
                WHEN 'TABLE'     THEN 1
                WHEN 'INDEX'     THEN 2
                WHEN 'VIEW'      THEN 3
                WHEN 'TRIGGER'   THEN 4
                WHEN 'PROCEDURE' THEN 5
                ELSE 99
            END,
            o.ObjectName;
        """;
}
