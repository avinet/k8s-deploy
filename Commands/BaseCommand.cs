using System.Net;
using System.Text;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Tomlyn;
using Tomlyn.Model;

namespace K8sDeploy;

public abstract class BaseCommand
{
    public enum CommandMode
    {
        Initialize,
        Update,
    };

    protected const string _auto_ns = "k8s-deploy-auto";

    protected CommandMode mode = CommandMode.Initialize;

    public HttpClient HttpClient = new HttpClient();

    private string _context;
    private string _deployment;
    private DirectoryInfo _manifests;
    private bool _dryRun;

    private TomlTable _values;
    private TomlTable _secrets;
    private TomlTable _clusterConfig;

    private Kubernetes _client;

    private YamlTemplateSerializer _yamlSerializer;

    public BaseCommand(FileInfo values, FileInfo? secrets, string? secretsFromEnv, DirectoryInfo manifests, bool dryRun)
    {
        _manifests = manifests;
        _dryRun = dryRun;

        if (!values.Exists)
            throw new Exception($"Values file {values.FullName} not found.");

        if (secrets == null && string.IsNullOrEmpty(secretsFromEnv))
        {
            if (mode == CommandMode.Initialize)
                throw new Exception("Either --secrets or --secrets-from-env must be specified when initializing a new deployment.");

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"No secrets available. Secrets will be verified presence of, but not created.");
            Console.ResetColor();
        }

        if (secrets != null && !string.IsNullOrEmpty(secretsFromEnv))
            throw new Exception("Only one of --secrets or --secrets-from-env may be specified.");

        if (secrets != null && !secrets.Exists)
            throw new Exception($"Secrets file {secrets.FullName} not found.");

        var tomlContents = File.ReadAllText(values.FullName);
        var tomlModel = Toml.Parse(tomlContents, values.FullName);
        _values = tomlModel.ToModel();
        _deployment = _values["deployment"] as string ?? "";
        _clusterConfig = _values["cluster"] as TomlTable ?? new TomlTable();
        _context = _clusterConfig["context"] as string ?? "";

        if (string.IsNullOrWhiteSpace(_deployment))
            throw new Exception("deployment not defined in values file.");

        if (string.IsNullOrWhiteSpace(_context))
            throw new Exception("cluster.context not defined in values file.");

        if (!string.IsNullOrEmpty(secretsFromEnv))
        {
            var secretsEnvContent = Environment.GetEnvironmentVariable(secretsFromEnv);
            if (secretsEnvContent == null)
                throw new Exception($"Secrets from environment variable {secretsFromEnv} not found.");

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Using secrets from env: {secretsFromEnv}");
            Console.ResetColor();
            var secretsModel = Toml.Parse(secretsEnvContent);
            _secrets = secretsModel.ToModel();
        }
        else if (secrets != null)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"Using secrets from file: {secrets.FullName}");
            Console.ResetColor();
            var secretsContents = File.ReadAllText(secrets.FullName);
            var secretsModel = Toml.Parse(secretsContents, secrets.FullName);
            _secrets = secretsModel.ToModel();
        }
        else
        {
            _secrets = new TomlTable();
        }

        _yamlSerializer = new YamlTemplateSerializer(_values, _secrets);

        var c = Utils.KubectlSetContext(_context, false);
        c.Wait();
        if (!c.Result)
            throw new Exception("Could not set the requested context.");

        KubernetesClientConfiguration config;
        config = KubernetesClientConfiguration.BuildDefaultConfig();
        if (config.CurrentContext != _context)
            throw new Exception("Could not get the requested context config.");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Kubectl context is {config.CurrentContext}");
        Console.ResetColor();

        _client = new Kubernetes(config);
    }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        await Utils.KubernetesVersionCheck(_client);

        if (!_manifests.Exists)
        {
            Console.Write($"manifests directory {_manifests.FullName} not found");
            return 1;
        }

        try
        {
            var ns = await _client.ReadNamespaceAsync(_deployment, false, cancellationToken);

            if (mode == CommandMode.Initialize)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{_deployment} namespace exists. Use k8s-deploy update to update the deployment.");
                Console.ResetColor();
                return 1;
            }
        }
        catch (HttpOperationException ex)
        {
            if (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                if (mode == CommandMode.Update)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"{_deployment} namespace does not exist. This cluster does not contain this deployment. Use k8s-deploy init to get started.");
                    Console.ResetColor();
                    return 1;
                }
                else
                {
                    var ns = new V1Namespace
                    {
                        Kind = "Namespace",
                        ApiVersion = "v1",
                        Metadata = new V1ObjectMeta
                        {
                            Name = _deployment,
                            Labels = new Dictionary<string, string> { { "name", _deployment } }
                        }
                    };
                    await _client.CreateNamespaceAsync(ns, _dryRun ? "All" : null, cancellationToken: cancellationToken);
                }
            }
            else
            {
                throw;
            }
        }

        try
        {
            var ns = await _client.ReadNamespaceAsync(_auto_ns, false, cancellationToken);
        }
        catch (HttpOperationException ex)
        {
            if (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"{_auto_ns} namespace does not exist. Creating.");
                await _client.CreateNamespaceAsync(new k8s.Models.V1Namespace()
                {

                    Kind = "Namespace",
                    ApiVersion = "v1",
                    Metadata = new k8s.Models.V1ObjectMeta
                    {
                        Name = _auto_ns
                    }
                }, dryRun: _dryRun ? "All" : null);
            }
            else
            {
                throw;
            }
        }

        var tmpDir = new DirectoryInfo(Path.Combine(_manifests.FullName, ".tmp"));
        if (!tmpDir.Exists)
        {
            tmpDir.Create();
        }
        else
        {
            tmpDir.Delete(true);
            tmpDir.Create();
        }

        foreach (var dir in _manifests.EnumerateDirectories().OrderBy(s => s.Name))
        {
            if (dir.Name[0] == '.') continue;

            if (dir.Name == "@init" && mode != CommandMode.Initialize)
                continue; // Only used when intializing a new cluster

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Processing directory {dir.Name}");
            Console.ForegroundColor = ConsoleColor.Gray;

            if (dir.Name == "@import")
            {
                foreach (var file in dir.EnumerateFiles().OrderBy(s => s.Name))
                {
                    Console.WriteLine($"- Processing import {file.Name}");
                    await ProcessImport(file, cancellationToken);
                }
                continue;
            }

            foreach (var subDir in dir.EnumerateDirectories().OrderBy(s => s.Name))
            {
                if (subDir.Name == "@secrets")
                {
                    foreach (var file in subDir.EnumerateFiles().OrderBy(s => s.Name))
                    {
                        Console.WriteLine($"- Processing secret {file.Name}");
                        await ProcessSecret(file, Path.Combine(tmpDir.FullName, $"{dir.Name}.0.{subDir.Name}.{file.Name}"), cancellationToken);
                    }
                    continue;
                }
                else if (subDir.Name == (_clusterConfig["variant"] as string ?? "generic"))
                {
                    foreach (var file in subDir.EnumerateFiles().OrderBy(s => s.Name))
                    {
                        Console.WriteLine($"- Processing {subDir.Name} variant file {file.Name}");
                        await _yamlSerializer.TransformYamlTemplate(file.FullName, Path.Combine(tmpDir.FullName, $"{dir.Name}.1.{subDir.Name}.{file.Name}"), false);
                    }
                }
            }

            foreach (var file in dir.EnumerateFiles().OrderBy(s => s.Name))
            {
                Console.WriteLine($"- Processing file {file.Name}");
                await _yamlSerializer.TransformYamlTemplate(file.FullName, Path.Combine(tmpDir.FullName, $"{dir.Name}.2.{file.Name}"), false);
            }

            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Applying generated manifests.");
        Console.ResetColor();
        Console.WriteLine();

        if (!await Utils.KubectlApply(tmpDir.FullName, _context, _dryRun))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Could not run kubectl apply");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Cluster configuration completed {(_dryRun ? "(dry run)" : "")}");
        Console.ResetColor();
        Console.WriteLine();

        return 0;
    }

    private async Task ProcessImport(FileInfo file, CancellationToken cancellationToken)
    {
        var configMapName = "import-" + file.Name;
        var import = (await File.ReadAllTextAsync(file.FullName, Encoding.UTF8, cancellationToken)).Trim();
        var isUpgrade = false;
        try
        {
            var configMap = await _client.ReadNamespacedConfigMapAsync(configMapName, _auto_ns, false, cancellationToken);
            if (configMap.Data["path"] == import)
            {
                Console.WriteLine($"  - {file.Name} is up to date.");
                return;
            }
            isUpgrade = true;
        }
        catch (HttpOperationException ex)
        {
            if (ex.Response.StatusCode != HttpStatusCode.NotFound)
                throw;
        }

        Console.WriteLine($"  - Importing {import}");

        if (!await Utils.KubectlApply(import, _context, _dryRun))
            throw new Exception($"Could not apply {import}");

        try
        {
            var configMap = new V1ConfigMap
            {
                ApiVersion = "v1",
                Metadata = new V1ObjectMeta
                {
                    Name = configMapName,
                    NamespaceProperty = _auto_ns
                },
                Data = new Dictionary<string, string> {
                    { "path", import }
                }
            };

            if (isUpgrade)
            {
                await _client.ReplaceNamespacedConfigMapAsync(configMap, configMapName, _auto_ns, dryRun: _dryRun ? "All" : null, cancellationToken: cancellationToken);
            }
            else
            {
                await _client.CreateNamespacedConfigMapAsync(configMap, _auto_ns, dryRun: _dryRun ? "All" : null, cancellationToken: cancellationToken);
            }
        }
        catch (HttpOperationException ex)
        {
            Utils.LogHttpOperationException(ex);
            throw;
        }
    }

    private async Task ProcessSecret(FileInfo file, string path, CancellationToken cancellationToken)
    {
        try
        {
            await _yamlSerializer.TransformYamlTemplate(file.FullName, path, true);
        }
        catch (YamlTemplateException)
        {
            if (mode == CommandMode.Initialize)
                throw;

            // TODO Add presence checks
        }
    }
}