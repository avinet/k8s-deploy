using System.CommandLine;

var manifestsOption = new Option<DirectoryInfo>(name: "--manifests", description: "The directory path where the templated manifest structure resides.");
manifestsOption.AddAlias("-m");
manifestsOption.IsRequired = true;

var valuesOption = new Option<FileInfo>(name: "--values", description: "Path to the .toml file containing the deployment configuration.");
valuesOption.AddAlias("-v");
valuesOption.IsRequired = true;

var secretsOption = new Option<string>(name: "--secrets", description: "Path to the .toml file containing the deployment secrets.");
secretsOption.AddAlias("-s");

var secretsFromEnvOption = new Option<string>(name: "--secrets-from-env", description: "Key of the environment variable containing the secrets in TOML format.");

var dryRunOption = new Option<bool>(name: "--dry-run", description: "If set, no changes will be applied to the cluster.");

var rootCommand = new RootCommand("k8s-deploy - a cluster management tool.");
rootCommand.AddGlobalOption(manifestsOption);
rootCommand.AddGlobalOption(valuesOption);
rootCommand.AddGlobalOption(secretsOption);
rootCommand.AddGlobalOption(secretsFromEnvOption);
rootCommand.AddGlobalOption(dryRunOption);

var returnCode = 0;

var initCommand = new Command("init", "Create a new deployment");
initCommand.SetHandler(async (invocationContext) =>
{
    try
    {
        var values = invocationContext.ParseResult.GetValueForOption(valuesOption);
        var secrets = invocationContext.ParseResult.GetValueForOption(secretsOption);
        var secretsFromEnv = invocationContext.ParseResult.GetValueForOption(secretsFromEnvOption);
        var manifests = invocationContext.ParseResult.GetValueForOption(manifestsOption)!;
        var dryRun = invocationContext.ParseResult.GetValueForOption(dryRunOption);
        returnCode = await new K8sDeploy.InitCommand(
            values!,
            string.IsNullOrWhiteSpace(secrets) ? null : new FileInfo(secrets),
            secretsFromEnv,
            manifests,
            dryRun
        ).Run(invocationContext.GetCancellationToken());
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        returnCode = 1;
    }
});
rootCommand.Add(initCommand);

var updateCommand = new Command("update", "Update an existing deployment");
updateCommand.SetHandler(async (invocationContext) =>
{
    try
    {
        var values = invocationContext.ParseResult.GetValueForOption(valuesOption);
        var secrets = invocationContext.ParseResult.GetValueForOption(secretsOption);
        var secretsFromEnv = invocationContext.ParseResult.GetValueForOption(secretsFromEnvOption);
        var manifests = invocationContext.ParseResult.GetValueForOption(manifestsOption)!;
        var dryRun = invocationContext.ParseResult.GetValueForOption(dryRunOption);
        returnCode = await new K8sDeploy.UpdateCommand(
            values!,
            string.IsNullOrWhiteSpace(secrets) ? null : new FileInfo(secrets),
            secretsFromEnv,
            manifests,
            dryRun
        ).Run(invocationContext.GetCancellationToken());
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.ResetColor();
        returnCode = 1;
    }
});
rootCommand.Add(updateCommand);

try
{
    await K8sDeploy.Utils.EnsureKubectlPresence();
    await rootCommand.InvokeAsync(args);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(ex.Message);
    Console.ResetColor();
    returnCode = 1;
}

return returnCode;