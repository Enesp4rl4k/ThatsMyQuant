using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Infrastructure.Persistence;
using ThatsMyQuant.Contracts.Models;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var optionsBuilder = new DbContextOptionsBuilder<QuantDbContext>();
optionsBuilder.UseSqlite("Data Source=C:\\Users\\parlak\\AppData\\Local\\ThatsMyQuant\\quant.db");

using var db = new QuantDbContext(optionsBuilder.Options);

var signalCount = await db.Signals.CountAsync();
var tradeCount = await db.Trades.CountAsync();

Console.WriteLine($"Signals: {signalCount}");
Console.WriteLine($"Trades: {tradeCount}");

var lastSignals = await db.Signals.OrderByDescending(s => s.Id).Take(5).ToListAsync();
foreach (var s in lastSignals)
{
    Console.WriteLine($"Signal: {s.Id} | Symbol: {s.Symbol} | Status: {s.Status}");
}

var lastTrades = await db.Trades.OrderByDescending(t => t.EntryTime).Take(5).ToListAsync();
foreach (var t in lastTrades)
{
    Console.WriteLine($"Trade: {t.Id} | Symbol: {t.Symbol} | Status: {t.Status} | Lifecycle: {t.LifecycleStatus}");
}
