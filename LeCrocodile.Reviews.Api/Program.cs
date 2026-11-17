using LeCrocodile.Reviews.Api;

if (args is ["--inspect", string inspectedPath])
{
    Console.WriteLine($"Database integrity: ok; schema: 1; reviews: {DatabaseMaintenance.Inspect(inspectedPath)}.");
    return;
}

if (args is ["--import", string sourcePath, string destinationPath])
{
    Console.WriteLine($"Database import completed; reviews: {DatabaseMaintenance.Import(sourcePath, destinationPath)}.");
    return;
}

if (args.Contains("--inspect") || args.Contains("--import"))
{
    throw new ArgumentException("Use --inspect DATABASE or --import SOURCE NEW_DESTINATION with the server stopped.");
}

await using WebApplication application = ServerApplication.Build(args);
if (application.Configuration["backup"] is string backupPath)
{
    application.Services.GetRequiredService<ReviewRepository>().Backup(backupPath);
    Console.WriteLine("The SQLite backup completed successfully.");
    return;
}

await application.RunAsync();
