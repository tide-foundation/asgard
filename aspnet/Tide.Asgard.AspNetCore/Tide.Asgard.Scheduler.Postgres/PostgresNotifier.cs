// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Npgsql;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.Postgres;

// Wakes workers across processes. NOTIFY goes out through the data source, LISTEN
// sits on a connection of its own, because notifications are delivered to the
// session that issued the LISTEN and a pooled connection would deliver them to
// whichever caller happened to get it next.
public sealed class PostgresNotifier : IJobNotifier, IAsyncDisposable
{
	public const string JobChannel = "asgard_jobs";

	private readonly NpgsqlDataSource _dataSource;
	private readonly SemaphoreSlim _listenerGate = new(1, 1);
	private NpgsqlConnection? _listener;

	public PostgresNotifier(NpgsqlDataSource dataSource) => _dataSource = dataSource;

	public async Task NotifyAsync(CancellationToken ct = default)
	{
		try
		{
			await using var command = _dataSource.CreateCommand($"select pg_notify('{JobChannel}', '')");
			await command.ExecuteNonQueryAsync(ct);
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			// Deliberately swallowed. A worker that cannot announce new work
			// still enqueued it, and the next poll finds it. Latency, not
			// correctness.
		}
	}

	public async Task WaitAsync(long timeoutMs, CancellationToken ct = default)
	{
		if (ct.IsCancellationRequested) return;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

		try
		{
			var listener = await ListenerAsync(ct);

			// Returns as soon as a notification arrives on any listened channel,
			// or when the linked token fires at the timeout.
			await listener.WaitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			// Timed out or shutting down. Both are normal.
		}
		catch (Exception)
		{
			// A dropped connection means notifications stop arriving. Discard it
			// so the next wait reconnects, and fall back to the timeout, which is
			// what a worker without a notifier does anyway.
			await DiscardListenerAsync();
			await Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None);
		}
	}

	private async Task<NpgsqlConnection> ListenerAsync(CancellationToken ct)
	{
		await _listenerGate.WaitAsync(ct);
		try
		{
			if (_listener is { State: System.Data.ConnectionState.Open }) return _listener;

			_listener = await _dataSource.OpenConnectionAsync(ct);
			await using var command = new NpgsqlCommand($"listen {JobChannel}", _listener);
			await command.ExecuteNonQueryAsync(ct);
			return _listener;
		}
		finally
		{
			_listenerGate.Release();
		}
	}

	private async Task DiscardListenerAsync()
	{
		await _listenerGate.WaitAsync();
		try
		{
			if (_listener is not null) await _listener.DisposeAsync();
			_listener = null;
		}
		finally
		{
			_listenerGate.Release();
		}
	}

	public async ValueTask DisposeAsync()
	{
		await DiscardListenerAsync();
		_listenerGate.Dispose();
	}
}
