namespace K8sDeploy;

public class UpdateCommand : BaseCommand
{
    public UpdateCommand(FileInfo values, FileInfo? secrets, string? secretsFromEnv, DirectoryInfo manifests, bool dryRun)
        : base(values, secrets, secretsFromEnv, manifests, dryRun)
    {
        mode = CommandMode.Update;
    }
}
