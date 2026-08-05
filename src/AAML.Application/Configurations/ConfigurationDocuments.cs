using AAML.Domain.Mods;

namespace AAML.Application.Configurations;

/// <summary>Stable identity for one configuration document belonging to one physical mod.</summary>
public sealed record ConfigurationDocumentId(ModKey Mod, string RelativePath);

public enum ConfigurationEncoding { Utf8, Utf8Bom, Utf16LittleEndian, Utf16BigEndian, Windows1252 }
public enum NewLineStyle { None, Lf, CrLf, Cr, Mixed }

public sealed record ConfigurationTextFormat(ConfigurationEncoding Encoding, NewLineStyle NewLines);

/// <summary>Accepted disk baseline with an opaque optimistic-concurrency revision.</summary>
public sealed record ConfigurationFileVersion(
    ConfigurationDocumentId Id,
    string Text,
    ConfigurationTextFormat Format,
    string Revision);

/// <summary>User-captured durable content, distinct from disk baseline and recovery backup.</summary>
public sealed record SavedConfigurationSnapshot(
    ConfigurationDocumentId Id,
    string Text,
    ConfigurationTextFormat Format);

public sealed record ConfigurationSaveReceipt(string Revision, bool RecoveryBackupCreated, ConfigurationTextFormat? Format = null);

public sealed record ConfigurationFileLimits(long MaxBytes, int MaxCharacters, int MaxLines);

public sealed record ConfigurationDocumentSummary(ConfigurationDocumentId Id, string ModName, string RelativePath);
