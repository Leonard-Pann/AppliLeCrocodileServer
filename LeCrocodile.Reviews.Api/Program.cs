using LeCrocodile.Reviews.Api;

await using WebApplication application = ServerApplication.Build(args);
if (application.Configuration["backup"] is string backupPath)
{
    application.Services.GetRequiredService<ReviewRepository>().Backup(backupPath);
    Console.WriteLine("The SQLite backup completed successfully.");
    return;
}

await application.RunAsync();
