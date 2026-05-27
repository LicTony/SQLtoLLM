DECLARE @Objects TABLE
(
    ObjectName SYSNAME COLLATE DATABASE_DEFAULT,
    ObjectType VARCHAR(20) COLLATE DATABASE_DEFAULT
);

/*
-- Ejemplo:
INSERT INTO @Objects VALUES
('dbo.Asignacion_TRBAN', 'TABLE'),
('dbo.SP_CHQ_HL_GENERACION_GL', 'PROCEDURE'),
('dbo.VW_Saldos', 'VIEW'),
('dbo.IX_Clientes_DNI', 'INDEX'),
('dbo.TR_Clientes_Audit', 'TRIGGER');
*/




SELECT
    o.ObjectType,
    o.ObjectName,
    ContextText =
    (
        CASE o.ObjectType

        /* ======================= TABLE ======================= */
        WHEN 'TABLE' THEN
            '--- TABLE ' + o.ObjectName + ' ---' + CHAR(10) +
            'CREATE TABLE ' + o.ObjectName + ' (' + CHAR(10) +

            STUFF((
                SELECT
                    ',' + CHAR(10) +
                    '  ' + c.name COLLATE DATABASE_DEFAULT + ' ' +
                    t.name COLLATE DATABASE_DEFAULT +
                    CASE 
                        WHEN t.name IN ('varchar','char','nvarchar','nchar')
                            THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX'
                                            ELSE CAST(c.max_length AS VARCHAR) END + ')'
                        ELSE ''
                    END +
                    CASE WHEN c.is_identity = 1 THEN ' IDENTITY' ELSE '' END +
                    CASE WHEN c.is_nullable = 0 THEN ' NOT NULL' ELSE ' NULL' END +
                    ISNULL(' DEFAULT ' + dc.definition COLLATE DATABASE_DEFAULT, '')
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc
                    ON dc.parent_object_id = c.object_id
                   AND dc.parent_column_id = c.column_id
                WHERE c.object_id = obj.object_id
                FOR XML PATH(''), TYPE
            ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '')

            + ISNULL(
                (
                    SELECT
                        ',' + CHAR(10) +
                        '  CONSTRAINT ' + kc.name COLLATE DATABASE_DEFAULT +
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
                ),
                ''
            )
            + CHAR(10) + ');' + CHAR(10) +

            /* -------- FOREIGN KEYS -------- */
            ISNULL(
            (
                STUFF((
                    SELECT
                        CHAR(10) +
                        'ALTER TABLE ' + o.ObjectName +
                        ' ADD CONSTRAINT ' + fk.name COLLATE DATABASE_DEFAULT +
                        ' FOREIGN KEY (' + pc.name COLLATE DATABASE_DEFAULT + ')' +
                        ' REFERENCES ' +
                        SCHEMA_NAME(rt.schema_id) + '.' +
                        rt.name COLLATE DATABASE_DEFAULT +
                        ' (' + rc.name COLLATE DATABASE_DEFAULT + ');'
                    FROM sys.foreign_keys fk
                    JOIN sys.foreign_key_columns fkc
                        ON fkc.constraint_object_id = fk.object_id
                    JOIN sys.columns pc
                        ON pc.object_id = fkc.parent_object_id
                       AND pc.column_id = fkc.parent_column_id
                    JOIN sys.columns rc
                        ON rc.object_id = fkc.referenced_object_id
                       AND rc.column_id = fkc.referenced_column_id
                    JOIN sys.tables rt
                        ON rt.object_id = fkc.referenced_object_id
                    WHERE fk.parent_object_id = obj.object_id
                    FOR XML PATH(''), TYPE
                ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 1, '')
            ), '')

        /* ======================= VIEW ======================= */
        WHEN 'VIEW' THEN
            '--- VIEW ' + o.ObjectName + ' ---' + CHAR(10) +
            ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<Sin VIEW DEFINITION>>')

        /* ======================= PROCEDURE ======================= */
        WHEN 'PROCEDURE' THEN
            '--- STORED PROCEDURE ' + o.ObjectName + ' ---' + CHAR(10) +
            ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<Sin VIEW DEFINITION>>')

        /* ======================= INDEX ======================= */
        WHEN 'INDEX' THEN
            '--- INDEX ' + o.ObjectName + ' ---' + CHAR(10) +
            (
                SELECT
                    'CREATE ' +
                    CASE WHEN i.is_unique = 1 THEN 'UNIQUE ' ELSE '' END +
                    i.type_desc COLLATE DATABASE_DEFAULT +
                    ' INDEX ' + i.name COLLATE DATABASE_DEFAULT +
                    ' ON ' + s.name COLLATE DATABASE_DEFAULT + '.' +
                    t.name COLLATE DATABASE_DEFAULT +
                    ' (' +
                    STUFF((
                        SELECT ', ' + c.name COLLATE DATABASE_DEFAULT
                        FROM sys.index_columns ic
                        JOIN sys.columns c
                          ON c.object_id = ic.object_id
                         AND c.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id
                          AND ic.index_id = i.index_id
                          AND ic.key_ordinal > 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)') COLLATE DATABASE_DEFAULT, 1, 2, '') +
                    ');'
                FROM sys.indexes i
                JOIN sys.tables t ON t.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE i.name = PARSENAME(o.ObjectName, 1)
            )

        /* ======================= TRIGGER ======================= */
        WHEN 'TRIGGER' THEN
            '--- TRIGGER ' + o.ObjectName + ' ---' + CHAR(10) +
            ISNULL(m.definition COLLATE DATABASE_DEFAULT, '<<Sin VIEW DEFINITION>>')

        END
    ) COLLATE DATABASE_DEFAULT
FROM @Objects o
JOIN sys.objects obj
  ON CONCAT(SCHEMA_NAME(obj.schema_id), '.', obj.name) COLLATE DATABASE_DEFAULT
     = o.ObjectName
LEFT JOIN sys.sql_modules m
  ON m.object_id = obj.object_id
ORDER BY
    CASE o.ObjectType
        WHEN 'TABLE' THEN 1
        WHEN 'INDEX' THEN 2
        WHEN 'VIEW' THEN 3
        WHEN 'TRIGGER' THEN 4
        WHEN 'PROCEDURE' THEN 5
        ELSE 99
    END,
    o.ObjectName;