using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace K8sDeploy;

public static class Utils
{
    public static async Task<int> RunCommand(string path, string arguments, string? workingDirectory = null, bool silent = false, Action<string>? output = null)
    {
        var pi = new ProcessStartInfo()
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        if (!silent)
        {
            Console.WriteLine($"\"{path}\" {arguments}");
        }

        var process = Process.Start(pi);

        if (process == null)
            throw new Exception($"No process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var sb = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            sb.AppendLine(e.Data ?? "");
            if (!silent)
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine(e.Data);
                Console.ResetColor();
            }
        };

        if (!silent)
        {
            process.ErrorDataReceived += (s, e) =>
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine(e.Data);
                Console.ResetColor();
            };
        }

        await process.WaitForExitAsync();

        output?.Invoke(sb.ToString());

        return process.ExitCode;
    }

    public static string KubectlPath { get; private set; } = "kubectl";

    public static async Task EnsureKubectlPresence()
    {
        var isAvailableInPath = true;
        try
        {
            await RunCommand("kubectl", "version --output=json", silent: true);
        }
        catch
        {
            isAvailableInPath = false;
        }

        if (!isAvailableInPath)
        {
            var os = "linux";
            var ext = "";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                os = "darwin";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                os = "windows";
                ext = ".exe";
            }

            var downloadPath = $"https://storage.googleapis.com/kubernetes-release/release/{k8s.GeneratedApiVersion.SwaggerVersion}/bin/{os}/amd64/kubectl{ext}";

            using (var fs = new FileStream("./kubectl", FileMode.Create))
            using (var client = new HttpClient())
            {
                var stream = await client.GetStreamAsync(downloadPath);
                await stream.CopyToAsync(fs);
            }

            if (os != "windows")
            {
                await RunCommand("chmod", "+x kubectl", silent: true);
            }
            await RunCommand(KubectlPath, "version --output=json", silent: true);

            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine($"Using kubectl at: {KubectlPath}");
            Console.ResetColor();
        }
    }

    public static async Task KubernetesVersionCheck(Kubernetes client)
    {
        VersionInfo? kubectlVersion = null;
        await Utils.RunCommand(KubectlPath, "version --output=json", silent: true, output: (result) =>
        {
            var v = JsonSerializer.Deserialize<KubectlVersion>(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            kubectlVersion = v?.ClientVersion;
        });

        if (kubectlVersion == null)
            throw new Exception("Could not obtain kubectl version. Make sure kubectl is available.");

        var clientVersionInts = k8s.GeneratedApiVersion.SwaggerVersion.Substring(1).Split('.');
        var clientVersion = new VersionInfo() { Major = clientVersionInts[0], Minor = clientVersionInts[1], GitVersion = k8s.GeneratedApiVersion.SwaggerVersion };
        var clusterVersion = client.Version.GetCode();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"kubectl client version: {kubectlVersion?.GitVersion}");
        Console.WriteLine($"Integrated client version: {clientVersion.GitVersion}");
        Console.WriteLine($"Cluster version: {clusterVersion.GitVersion}");
        Console.ResetColor();

        if (kubectlVersion?.Major != clientVersion.Major || kubectlVersion?.Minor != clientVersion.Minor)
        {
            throw new Exception("kubectl and integrated client version is not the same.");
        }

        var versionDelta = Convert.ToInt32(clusterVersion.Minor) - Convert.ToInt32(clientVersion.Minor);
        if (clusterVersion.Major != clientVersion.Major || versionDelta < -2 || versionDelta > 0)
        {
            throw new Exception($"Cluster and integrated client versions are not compatible (client must be the same or up to two minor versions higher than cluster version). Minor version delta: {versionDelta}");
        }
    }

    public static void LogHttpOperationException(HttpOperationException ex)
    {
        Console.WriteLine($"HTTP {ex.Request.Method} {ex.Request.RequestUri}");
        foreach (var h in ex.Request.Headers)
        {
            Console.WriteLine($"  {h.Key}: {string.Join(", ", h.Value)}");
        }
        Console.WriteLine($"{ex.Response.StatusCode} : {ex.Response.Content}");
    }

    public static async Task<bool> KubectlSetContext(string context, bool silent = true)
    {
        return await Utils.RunCommand(KubectlPath, $"config use-context {context}", silent: silent) == 0;
    }

    public static async Task<bool> KubectlApply(string path, string context, bool dryRun)
    {
        if (!await KubectlSetContext(context))
            return false;

        return await Utils.RunCommand(KubectlPath, $"apply -f {path} {(dryRun ? $"--dry-run=client" : "")}") == 0;
    }
}
