namespace AAML.Domain.Launching;

/// <summary>Supported game execution runtimes for Linux.</summary>
public enum GameRuntime
{
    /// <summary>Automatically detect and use the available layout.</summary>
    Auto,
    /// <summary>Use the native Feral Linux executable.</summary>
    Native,
    /// <summary>Use the Windows executable through Proton.</summary>
    Proton
}