namespace AAML.Application;

public static class ProjectIdentity
{
    public const string GitHubOwner = "JakeRoxs";
    public const string GitHubRepository = "xcom2-dark-launcher";
    public static Uri RepositoryUri { get; } = new($"https://github.com/{GitHubOwner}/{GitHubRepository}");
    public static Uri IssuesUri { get; } = new($"https://github.com/{GitHubOwner}/{GitHubRepository}/issues");
    public static Uri WikiUri { get; } = new($"https://github.com/{GitHubOwner}/{GitHubRepository}/wiki");
    public static Uri LicenseUri { get; } = new($"https://github.com/{GitHubOwner}/{GitHubRepository}/blob/main/LICENSE");
    public static Uri ReleasesApiUri { get; } = new($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepository}/releases?per_page=20");
}
