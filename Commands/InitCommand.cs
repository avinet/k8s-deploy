namespace K8sDeploy;

public class InitCommand : BaseCommand
{
    public InitCommand(FileInfo values, FileInfo? secrets, string? secretsFromEnv, DirectoryInfo manifests, bool dryRun)
        : base(values, secrets, secretsFromEnv, manifests, dryRun)
    {
        mode = CommandMode.Initialize;
    }
}
