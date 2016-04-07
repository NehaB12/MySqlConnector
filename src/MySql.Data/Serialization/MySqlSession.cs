﻿using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace MySql.Data.Serialization
{
	internal sealed class MySqlSession : IDisposable
	{
		public MySqlSession(ConnectionPool pool)
		{
			Pool = pool;
		}

		public ServerVersion ServerVersion { get; set; }
		public ConnectionPool Pool { get; }
		public bool ReturnToPool() => Pool != null && Pool.Return(this);

		public void Dispose()
		{
			if (m_state == State.Connected)
			{
				m_transmitter.SendAsync(QuitPayload.Create(), CancellationToken.None).Wait();
				m_transmitter.TryReceiveReplyAsync(CancellationToken.None).Wait();
			}
			m_transmitter = null;
			if (m_socket != null)
			{
				if (m_socket.Connected)
					m_socket.Shutdown(SocketShutdown.Both);
				Utility.Dispose(ref m_socket);
			}
			m_state = State.Closed;
		}

		public async Task ConnectAsync(string hostname, int port)
		{
			m_socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
#if NETSTANDARD1_3
			await m_socket.ConnectAsync(hostname, port).ConfigureAwait(false);
#else
			await Task.Factory.FromAsync(m_socket.BeginConnect, m_socket.EndConnect, hostname, port, null).ConfigureAwait(false);
#endif
			m_transmitter = new PacketTransmitter(m_socket);
			m_state = State.Connected;
		}

		// Starts a new conversation with the server by sending the first packet.
		public Task SendAsync(PayloadData payload, CancellationToken cancellationToken)
			=> TryAsync(m_transmitter.SendAsync, payload, cancellationToken);

		// Starts a new conversation with the server by receiving the first packet.
		public Task<PayloadData> ReceiveAsync(CancellationToken cancellationToken)
			=> TryAsync(m_transmitter.ReceiveAsync, cancellationToken);

		// Continues a conversation with the server by receiving a response to a packet sent with 'Send' or 'SendReply'.
		public Task<PayloadData> ReceiveReplyAsync(CancellationToken cancellationToken)
			=> TryAsync(m_transmitter.ReceiveReplyAsync, cancellationToken);

		// Continues a conversation with the server by sending a reply to a packet received with 'Receive' or 'ReceiveReply'.
		public Task SendReplyAsync(PayloadData payload, CancellationToken cancellationToken)
			=> TryAsync(m_transmitter.SendReplyAsync, payload, cancellationToken);


		private void VerifyConnected()
		{
			if (m_state == State.Closed)
				throw new ObjectDisposedException(nameof(MySqlSession));
			if (m_state != State.Connected)
				throw new InvalidOperationException("MySqlSession is not connected.");
		}

		private async Task TryAsync<TArg>(Func<TArg, CancellationToken, Task> func, TArg arg, CancellationToken cancellationToken)
		{
			VerifyConnected();
			try
			{
				await func(arg, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception) when (SetFailed())
			{
			}
		}

		private async Task<TResult> TryAsync<TResult>(Func<CancellationToken, Task<TResult>> func, CancellationToken cancellationToken)
		{
			VerifyConnected();
			try
			{
				return await func(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception) when (SetFailed())
			{
				return default(TResult);
			}
		}

		private bool SetFailed()
		{
			m_state = State.Failed;
			return false;
		}

		private enum State
		{
			Created,
			Connected,
			Closed,
			Failed,
		}

		State m_state;
		Socket m_socket;
		PacketTransmitter m_transmitter;
	}
}
