namespace K8sDeploy;

public class KubectlVersion
{
    public k8s.Models.VersionInfo? ClientVersion { get; set; }
    public k8s.Models.VersionInfo? ServerVersion { get; set; }
    public string? KustomizeVersion { get; set; }
}