namespace SharpEngine.UI;

public sealed record UiViewModelContract(string Id, int Version,
    string RuntimeType, string JsonSchema)
{
    public string Key
    {
        get
        {
            return $"{Id}@{Version}";
        }
    }
}

public interface IUiRuntime
{
    IReadOnlyList<UiViewModelContract> ViewModelContracts
    {
        get;
    }

    IDisposable RegisterViewModel<T>(string Id, int Version) where T : class;
}
