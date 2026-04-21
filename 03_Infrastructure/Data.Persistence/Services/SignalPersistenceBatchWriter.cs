using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Infrastructure.Persistence;

namespace ThatsMyQuant.Infrastructure.Services;

internal static class SignalPersistenceBatchWriter
{
    private static readonly JsonSerializerOptions s_jsonOpts = new();

    private const string UpsertSql = """
        INSERT INTO Signals (
            Id, Source, Symbol, Side, Price, Status, RetryCount, Quantity, Confidence, Reasoning,
            StrategyTag, RegimeTag, MarketType, Exchange, Timestamp, ClaimedAtUtc, ClaimedBy, Volume24h, OrderFlowImbalance,
            BestBid, BestAsk, WallAgeMs, ExecutionAction, PriceActionSignal, ReasonFlags, Metadata
        )
        VALUES (
            $Id, $Source, $Symbol, $Side, $Price, $Status, $RetryCount, $Quantity, $Confidence, $Reasoning,
            $StrategyTag, $RegimeTag, $MarketType, $Exchange, $Timestamp, $ClaimedAtUtc, $ClaimedBy, $Volume24h, $OrderFlowImbalance,
            $BestBid, $BestAsk, $WallAgeMs, $ExecutionAction, $PriceActionSignal, $ReasonFlags, $Metadata
        )
        ON CONFLICT(Id) DO UPDATE SET
            Source = excluded.Source,
            Symbol = excluded.Symbol,
            Side = excluded.Side,
            Price = excluded.Price,
            Status = excluded.Status,
            RetryCount = excluded.RetryCount,
            Quantity = excluded.Quantity,
            Confidence = excluded.Confidence,
            Reasoning = excluded.Reasoning,
            StrategyTag = excluded.StrategyTag,
            RegimeTag = excluded.RegimeTag,
            MarketType = excluded.MarketType,
            Exchange = excluded.Exchange,
            Timestamp = excluded.Timestamp,
            ClaimedAtUtc = excluded.ClaimedAtUtc,
            ClaimedBy = excluded.ClaimedBy,
            Volume24h = excluded.Volume24h,
            OrderFlowImbalance = excluded.OrderFlowImbalance,
            BestBid = excluded.BestBid,
            BestAsk = excluded.BestAsk,
            WallAgeMs = excluded.WallAgeMs,
            ExecutionAction = excluded.ExecutionAction,
            PriceActionSignal = excluded.PriceActionSignal,
            ReasonFlags = excluded.ReasonFlags,
            Metadata = excluded.Metadata;
        """;

    public static async Task UpsertAsync(QuantDbContext db, Signal signal, CancellationToken ct = default)
        => await FlushAsync(db, [Clone(signal)], ct);

    public static async Task FlushAsync(QuantDbContext db, IReadOnlyList<Signal> signals, CancellationToken ct)
    {
        if (signals.Count == 0)
        {
            return;
        }

        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        await using var command = CreateUpsertCommand(connection, dbTransaction);
        foreach (var signal in signals)
        {
            BindSignal(command, signal);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public static Signal Clone(Signal signal)
    {
        return new Signal
        {
            Id = signal.Id,
            Source = signal.Source,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Price = signal.Price,
            Status = signal.Status,
            RetryCount = signal.RetryCount,
            Quantity = signal.Quantity,
            Confidence = signal.Confidence,
            Reasoning = signal.Reasoning,
            StrategyTag = signal.StrategyTag,
            RegimeTag = signal.RegimeTag,
            MarketType = signal.MarketType,
            Exchange = signal.Exchange,
            Timestamp = signal.Timestamp,
            ClaimedAtUtc = signal.ClaimedAtUtc,
            ClaimedBy = signal.ClaimedBy,
            Volume24h = signal.Volume24h,
            OrderFlowImbalance = signal.OrderFlowImbalance,
            BestBid = signal.BestBid,
            BestAsk = signal.BestAsk,
            WallAgeMs = signal.WallAgeMs,
            ExecutionAction = signal.ExecutionAction,
            PriceActionSignal = signal.PriceActionSignal,
            ReasonFlags = signal.ReasonFlags,
            Metadata = signal.Metadata.Count == 0
                ? []
                : signal.Metadata.ToDictionary(entry => entry.Key, entry => entry.Value)
        };
    }

    private static DbCommand CreateUpsertCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;

        AddParameter(command, "$Id");
        AddParameter(command, "$Source");
        AddParameter(command, "$Symbol");
        AddParameter(command, "$Side");
        AddParameter(command, "$Price");
        AddParameter(command, "$Status");
        AddParameter(command, "$RetryCount");
        AddParameter(command, "$Quantity");
        AddParameter(command, "$Confidence");
        AddParameter(command, "$Reasoning");
        AddParameter(command, "$StrategyTag");
        AddParameter(command, "$RegimeTag");
        AddParameter(command, "$MarketType");
        AddParameter(command, "$Exchange");
        AddParameter(command, "$Timestamp");
        AddParameter(command, "$ClaimedAtUtc");
        AddParameter(command, "$ClaimedBy");
        AddParameter(command, "$Volume24h");
        AddParameter(command, "$OrderFlowImbalance");
        AddParameter(command, "$BestBid");
        AddParameter(command, "$BestAsk");
        AddParameter(command, "$WallAgeMs");
        AddParameter(command, "$ExecutionAction");
        AddParameter(command, "$PriceActionSignal");
        AddParameter(command, "$ReasonFlags");
        AddParameter(command, "$Metadata");

        return command;
    }

    private static void BindSignal(DbCommand command, Signal signal)
    {
        SetParameter(command, "$Id", signal.Id);
        SetParameter(command, "$Source", signal.Source);
        SetParameter(command, "$Symbol", signal.Symbol);
        SetParameter(command, "$Side", signal.Side.ToString());
        SetParameter(command, "$Price", signal.Price);
        SetParameter(command, "$Status", signal.Status.ToString());
        SetParameter(command, "$RetryCount", signal.RetryCount);
        SetParameter(command, "$Quantity", signal.Quantity);
        SetParameter(command, "$Confidence", signal.Confidence);
        SetParameter(command, "$Reasoning", signal.Reasoning);
        SetParameter(command, "$StrategyTag", signal.StrategyTag);
        SetParameter(command, "$RegimeTag", signal.RegimeTag);
        SetParameter(command, "$MarketType", (int)signal.MarketType);
        SetParameter(command, "$Exchange", signal.Exchange);
        SetParameter(command, "$Timestamp", signal.Timestamp);
        SetParameter(command, "$ClaimedAtUtc", signal.ClaimedAtUtc);
        SetParameter(command, "$ClaimedBy", signal.ClaimedBy);
        SetParameter(command, "$Volume24h", signal.Volume24h);
        SetParameter(command, "$OrderFlowImbalance", signal.OrderFlowImbalance);
        SetParameter(command, "$BestBid", signal.BestBid);
        SetParameter(command, "$BestAsk", signal.BestAsk);
        SetParameter(command, "$WallAgeMs", signal.WallAgeMs);
        SetParameter(command, "$ExecutionAction", signal.ExecutionAction.ToString());
        SetParameter(command, "$PriceActionSignal", signal.PriceActionSignal.ToString());
        SetParameter(command, "$ReasonFlags", (int)signal.ReasonFlags);
        SetParameter(command, "$Metadata", JsonSerializer.Serialize(signal.Metadata, s_jsonOpts));
    }

    private static void AddParameter(DbCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
    }

    private static void SetParameter(DbCommand command, string name, object? value)
        => ((DbParameter)command.Parameters[name]!).Value = value ?? DBNull.Value;
}
