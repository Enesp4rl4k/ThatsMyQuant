using System;
using Microsoft.Extensions.Configuration;
using ThatsMyQuant.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var connString = config.GetConnectionString("Quant");
var resolved = QuantPathResolver.ResolveSqliteConnectionString(connString ?? "Data Source=quant.db");

Console.WriteLine($"Config ConnString: {connString}");
Console.WriteLine($"Resolved ConnString: {resolved}");
