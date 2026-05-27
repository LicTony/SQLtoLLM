using System.ComponentModel;

namespace SQLtoLLM.UI;

public class ObjectRow : INotifyPropertyChanged
{
    private string _objectName = string.Empty;
    private string _detectedType = string.Empty;
    private string _editableType = string.Empty;
    private string _status = string.Empty;

    public string ObjectName
    {
        get => _objectName;
        set { _objectName = value; OnPropertyChanged(nameof(ObjectName)); }
    }

    public string DetectedType
    {
        get => _detectedType;
        set { _detectedType = value; OnPropertyChanged(nameof(DetectedType)); }
    }

    public string EditableType
    {
        get => _editableType;
        set { _editableType = value; OnPropertyChanged(nameof(EditableType)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(nameof(Status)); }
    }

    public static readonly List<string> AvailableTypes = ["TABLE", "VIEW", "PROCEDURE", "INDEX", "TRIGGER"];

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
