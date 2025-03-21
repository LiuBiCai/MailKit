﻿//
// Pop3ClientTests.cs
//
// Author: Jeffrey Stedfast <jestedfa@microsoft.com>
//
// Copyright (c) 2013-2023 .NET Foundation and Contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Collections;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;

using MimeKit;

using MailKit;
using MailKit.Security;
using MailKit.Net.Pop3;
using MailKit.Net.Proxy;

using UnitTests.Security;
using UnitTests.Net.Proxy;

using AuthenticationException = MailKit.Security.AuthenticationException;

namespace UnitTests.Net.Pop3 {
	[TestFixture]
	public class Pop3ClientTests
	{
		static readonly Pop3Capabilities ComcastCapa1 = Pop3Capabilities.Expire | Pop3Capabilities.StartTLS |
			Pop3Capabilities.Top | Pop3Capabilities.UIDL | Pop3Capabilities.User;
		static readonly Pop3Capabilities ComcastCapa2 = Pop3Capabilities.Expire | Pop3Capabilities.StartTLS |
			Pop3Capabilities.Sasl | Pop3Capabilities.Top | Pop3Capabilities.UIDL | Pop3Capabilities.User;
		static readonly Pop3Capabilities ExchangeCapa = Pop3Capabilities.Sasl | Pop3Capabilities.Top |
			Pop3Capabilities.UIDL | Pop3Capabilities.User;
		static readonly Pop3Capabilities GMailCapa1 = Pop3Capabilities.User | Pop3Capabilities.ResponseCodes |
			Pop3Capabilities.Expire | Pop3Capabilities.LoginDelay | Pop3Capabilities.Top |
			Pop3Capabilities.UIDL | Pop3Capabilities.Sasl;
		static readonly Pop3Capabilities GMailCapa2 = Pop3Capabilities.User | Pop3Capabilities.ResponseCodes |
			Pop3Capabilities.Pipelining | Pop3Capabilities.Expire | Pop3Capabilities.LoginDelay |
			Pop3Capabilities.Top | Pop3Capabilities.UIDL;
		static readonly Pop3Capabilities LangCapa1 = Pop3Capabilities.User | Pop3Capabilities.ResponseCodes |
		    Pop3Capabilities.Expire | Pop3Capabilities.LoginDelay | Pop3Capabilities.Top |
		    Pop3Capabilities.UIDL | Pop3Capabilities.Sasl | Pop3Capabilities.UTF8 |
		    Pop3Capabilities.UTF8User | Pop3Capabilities.Lang | Pop3Capabilities.Apop;
		static readonly Pop3Capabilities LangCapa2 = Pop3Capabilities.User | Pop3Capabilities.ResponseCodes |
		    Pop3Capabilities.Pipelining | Pop3Capabilities.Expire | Pop3Capabilities.LoginDelay |
		    Pop3Capabilities.Top | Pop3Capabilities.UIDL | Pop3Capabilities.Lang | Pop3Capabilities.Apop;
		const CipherAlgorithmType GmxDeCipherAlgorithm = CipherAlgorithmType.Aes256;
		const int GmxDeCipherStrength = 256;
		const HashAlgorithmType GmxDeHashAlgorithm = HashAlgorithmType.Sha384;
		const ExchangeAlgorithmType EcdhEphemeral = (ExchangeAlgorithmType) 44550;

		static string HexEncode (byte[] digest)
		{
			var hex = new StringBuilder ();

			for (int i = 0; i < digest.Length; i++)
				hex.Append (digest[i].ToString ("x2"));

			return hex.ToString ();
		}

		[Test]
		public void TestArgumentExceptions ()
		{
			using (var client = new Pop3Client ()) {
				var credentials = new NetworkCredential ("username", "password");

				// ReplayConnect
				Assert.Throws<ArgumentNullException> (() => client.ReplayConnect (null, Stream.Null));
				Assert.Throws<ArgumentNullException> (() => client.ReplayConnect ("host", null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ReplayConnectAsync (null, Stream.Null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ReplayConnectAsync ("host", null));

				// Connect
				Assert.Throws<ArgumentNullException> (() => client.Connect ((Uri) null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync ((Uri) null));
				Assert.Throws<ArgumentException> (() => client.Connect (new Uri ("path", UriKind.Relative)));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.ConnectAsync (new Uri ("path", UriKind.Relative)));
				Assert.Throws<ArgumentNullException> (() => client.Connect (null, 110, false));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync (null, 110, false));
				Assert.Throws<ArgumentException> (() => client.Connect (string.Empty, 110, false));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.ConnectAsync (string.Empty, 110, false));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.Connect ("host", -1, false));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.ConnectAsync ("host", -1, false));
				Assert.Throws<ArgumentNullException> (() => client.Connect (null, 110, SecureSocketOptions.None));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync (null, 110, SecureSocketOptions.None));
				Assert.Throws<ArgumentException> (() => client.Connect (string.Empty, 110, SecureSocketOptions.None));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.ConnectAsync (string.Empty, 110, SecureSocketOptions.None));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.Connect ("host", -1, SecureSocketOptions.None));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.ConnectAsync ("host", -1, SecureSocketOptions.None));

				Assert.Throws<ArgumentNullException> (() => client.Connect ((Socket) null, "host", 110, SecureSocketOptions.None));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync ((Socket) null, "host", 110, SecureSocketOptions.None));
				Assert.Throws<ArgumentNullException> (() => client.Connect ((Stream) null, "host", 110, SecureSocketOptions.None));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync ((Stream) null, "host", 110, SecureSocketOptions.None));

				using (var socket = new Socket (AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
					Assert.Throws<ArgumentException> (() => client.Connect (socket, "host", 110, SecureSocketOptions.None));
					Assert.ThrowsAsync<ArgumentException> (async () => await client.ConnectAsync (socket, "host", 110, SecureSocketOptions.None));
				}

				// Authenticate
				Assert.Throws<ArgumentNullException> (() => client.Authenticate ((SaslMechanism) null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync ((SaslMechanism) null));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate ((ICredentials) null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync ((ICredentials) null));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (null, "password"));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (null, "password"));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate ("username", null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync ("username", null));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (null, credentials));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (null, credentials));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (Encoding.UTF8, null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (Encoding.UTF8, null));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (null, "username", "password"));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (null, "username", "password"));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (Encoding.UTF8, null, "password"));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (Encoding.UTF8, null, "password"));
				Assert.Throws<ArgumentNullException> (() => client.Authenticate (Encoding.UTF8, "username", null));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.AuthenticateAsync (Encoding.UTF8, "username", null));
			}
		}

		static void AssertDefaultValues (string host, int port, SecureSocketOptions options, Uri expected)
		{
			Pop3Client.ComputeDefaultValues (host, ref port, ref options, out Uri uri, out bool starttls);

			if (expected.PathAndQuery == "/?starttls=when-available") {
				Assert.AreEqual (SecureSocketOptions.StartTlsWhenAvailable, options, "{0}", expected);
				Assert.IsTrue (starttls, "{0}", expected);
			} else if (expected.PathAndQuery == "/?starttls=always") {
				Assert.AreEqual (SecureSocketOptions.StartTls, options, "{0}", expected);
				Assert.IsTrue (starttls, "{0}", expected);
			} else if (expected.Scheme == "pops") {
				Assert.AreEqual (SecureSocketOptions.SslOnConnect, options, "{0}", expected);
				Assert.IsFalse (starttls, "{0}", expected);
			} else {
				Assert.AreEqual (SecureSocketOptions.None, options, "{0}", expected);
				Assert.IsFalse (starttls, "{0}", expected);
			}

			Assert.AreEqual (expected.ToString (), uri.ToString ());
			Assert.AreEqual (expected.Port, port, "{0}", expected);
		}

		[Test]
		public void TestComputeDefaultValues ()
		{
			const string host = "pop.skyfall.net";

			AssertDefaultValues (host, 0, SecureSocketOptions.None, new Uri ($"pop://{host}:110"));
			AssertDefaultValues (host, 110, SecureSocketOptions.None, new Uri ($"pop://{host}:110"));
			AssertDefaultValues (host, 995, SecureSocketOptions.None, new Uri ($"pop://{host}:995"));

			AssertDefaultValues (host, 0, SecureSocketOptions.SslOnConnect, new Uri ($"pops://{host}:995"));
			AssertDefaultValues (host, 110, SecureSocketOptions.SslOnConnect, new Uri ($"pops://{host}:110"));
			AssertDefaultValues (host, 995, SecureSocketOptions.SslOnConnect, new Uri ($"pops://{host}:995"));

			AssertDefaultValues (host, 0, SecureSocketOptions.StartTls, new Uri ($"pop://{host}:110/?starttls=always"));
			AssertDefaultValues (host, 110, SecureSocketOptions.StartTls, new Uri ($"pop://{host}:110/?starttls=always"));
			AssertDefaultValues (host, 995, SecureSocketOptions.StartTls, new Uri ($"pop://{host}:995/?starttls=always"));

			AssertDefaultValues (host, 0, SecureSocketOptions.StartTlsWhenAvailable, new Uri ($"pop://{host}:110/?starttls=when-available"));
			AssertDefaultValues (host, 110, SecureSocketOptions.StartTlsWhenAvailable, new Uri ($"pop://{host}:110/?starttls=when-available"));
			AssertDefaultValues (host, 995, SecureSocketOptions.StartTlsWhenAvailable, new Uri ($"pop://{host}:995/?starttls=when-available"));

			AssertDefaultValues (host, 0, SecureSocketOptions.Auto, new Uri ($"pop://{host}:110/?starttls=when-available"));
			AssertDefaultValues (host, 110, SecureSocketOptions.Auto, new Uri ($"pop://{host}:110/?starttls=when-available"));
			AssertDefaultValues (host, 995, SecureSocketOptions.Auto, new Uri ($"pops://{host}:995"));
		}

		static Socket Connect (string host, int port)
		{
			var ipAddresses = Dns.GetHostAddresses (host);
			Socket socket = null;

			for (int i = 0; i < ipAddresses.Length; i++) {
				socket = new Socket (ipAddresses[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);

				try {
					socket.Connect (ipAddresses[i], port);
					break;
				} catch {
					socket.Dispose ();
					socket = null;
				}
			}

			return socket;
		}

		[Test]
		public void TestSslHandshakeExceptions ()
		{
			using (var client = new Pop3Client ()) {
				Socket socket;

				// 1. Test connecting to a non-SSL port fails with an SslHandshakeException.
				Assert.Throws<SslHandshakeException> (() => client.Connect ("www.gmail.com", 80, true));

				socket = Connect ("www.gmail.com", 80);
				Assert.Throws<SslHandshakeException> (() => client.Connect (socket, "www.gmail.com", 80, SecureSocketOptions.SslOnConnect));

				// 2. Test connecting to a server with a bad SSL certificate fails with an SslHandshakeException.
				try {
					client.Connect ("untrusted-root.badssl.com", 443, SecureSocketOptions.SslOnConnect);
					Assert.Fail ("SSL handshake should have failed with untrusted-root.badssl.com.");
				} catch (SslHandshakeException ex) {
					Assert.NotNull (ex.ServerCertificate, "ServerCertificate");
					SslHandshakeExceptionTests.AssertServerCertificate ((X509Certificate2) ex.ServerCertificate);

					// Note: This is null on Mono because Mono provides an empty chain.
					if (ex.RootCertificateAuthority is X509Certificate2 root)
						SslHandshakeExceptionTests.AssertRootCertificate (root);
				} catch (Exception ex) {
					Assert.Ignore ("SSL handshake failure inconclusive: {0}", ex);
				}

				try {
					socket = Connect ("untrusted-root.badssl.com", 443);
					client.Connect (socket, "untrusted-root.badssl.com", 443, SecureSocketOptions.SslOnConnect);
					Assert.Fail ("SSL handshake should have failed with untrusted-root.badssl.com.");
				} catch (SslHandshakeException ex) {
					Assert.NotNull (ex.ServerCertificate, "ServerCertificate");
					SslHandshakeExceptionTests.AssertServerCertificate ((X509Certificate2) ex.ServerCertificate);

					// Note: This is null on Mono because Mono provides an empty chain.
					if (ex.RootCertificateAuthority is X509Certificate2 root)
						SslHandshakeExceptionTests.AssertRootCertificate (root);
				} catch (Exception ex) {
					Assert.Ignore ("SSL handshake failure inconclusive: {0}", ex);
				}
			}
		}

		[Test]
		public async Task TestSslHandshakeExceptionsAsync ()
		{
			using (var client = new Pop3Client ()) {
				Socket socket;

				// 1. Test connecting to a non-SSL port fails with an SslHandshakeException.
				Assert.ThrowsAsync<SslHandshakeException> (async () => await client.ConnectAsync ("www.gmail.com", 80, true));

				socket = Connect ("www.gmail.com", 80);
				Assert.ThrowsAsync<SslHandshakeException> (async () => await client.ConnectAsync (socket, "www.gmail.com", 80, SecureSocketOptions.SslOnConnect));

				// 2. Test connecting to a server with a bad SSL certificate fails with an SslHandshakeException.
				try {
					await client.ConnectAsync ("untrusted-root.badssl.com", 443, SecureSocketOptions.SslOnConnect);
					Assert.Fail ("SSL handshake should have failed with untrusted-root.badssl.com.");
				} catch (SslHandshakeException ex) {
					Assert.NotNull (ex.ServerCertificate, "ServerCertificate");
					SslHandshakeExceptionTests.AssertServerCertificate ((X509Certificate2) ex.ServerCertificate);

					// Note: This is null on Mono because Mono provides an empty chain.
					if (ex.RootCertificateAuthority is X509Certificate2 root)
						SslHandshakeExceptionTests.AssertRootCertificate (root);
				} catch (Exception ex) {
					Assert.Ignore ("SSL handshake failure inconclusive: {0}", ex);
				}

				try {
					socket = Connect ("untrusted-root.badssl.com", 443);
					await client.ConnectAsync (socket, "untrusted-root.badssl.com", 443, SecureSocketOptions.SslOnConnect);
					Assert.Fail ("SSL handshake should have failed with untrusted-root.badssl.com.");
				} catch (SslHandshakeException ex) {
					Assert.NotNull (ex.ServerCertificate, "ServerCertificate");
					SslHandshakeExceptionTests.AssertServerCertificate ((X509Certificate2) ex.ServerCertificate);

					// Note: This is null on Mono because Mono provides an empty chain.
					if (ex.RootCertificateAuthority is X509Certificate2 root)
						SslHandshakeExceptionTests.AssertRootCertificate (root);
				} catch (Exception ex) {
					Assert.Ignore ("SSL handshake failure inconclusive: {0}", ex);
				}
			}
		}

		[Test]
		public void TestSyncRoot ()
		{
			using (var client = new Pop3Client ()) {
				Assert.IsInstanceOf<Pop3Engine> (client.SyncRoot);
			}
		}

		[Test]
		public void TestInvalidStateExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				Assert.Throws<ServiceNotConnectedException> (() => client.Authenticate ("username", "password"));
				Assert.Throws<ServiceNotConnectedException> (() => client.Authenticate (new NetworkCredential ("username", "password")));
				Assert.Throws<ServiceNotConnectedException> (() => client.Authenticate (new SaslMechanismPlain ("username", "password")));

				Assert.Throws<ServiceNotConnectedException> (() => client.EnableUTF8 ());
				Assert.Throws<ServiceNotConnectedException> (() => client.GetLanguages ());
				Assert.Throws<ServiceNotConnectedException> (() => client.SetLanguage ("en"));

				Assert.Throws<ServiceNotConnectedException> (() => client.NoOp ());

				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageSizes ());
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageSize (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageUids ());
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageUid (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessage (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessages (0, 1));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessages (new int[] { 0 }));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageHeaders (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageHeaders (0, 1));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetMessageHeaders (new int[] { 0 }));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetStream (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetStreams (0, 1));
				Assert.Throws<ServiceNotConnectedException> (() => client.GetStreams (new int[] { 0 }));
				Assert.Throws<ServiceNotConnectedException> (() => client.DeleteMessage (0));
				Assert.Throws<ServiceNotConnectedException> (() => client.DeleteMessages (0, 1));
				Assert.Throws<ServiceNotConnectedException> (() => client.DeleteMessages (new int [] { 0 }));

				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);
				Assert.AreEqual (0, client.LoginDelay);

				Assert.Throws<ArgumentException> (() => client.Capabilities |= Pop3Capabilities.Apop);
				Assert.DoesNotThrow (() => client.Capabilities &= ~Pop3Capabilities.UIDL);

				Assert.Throws<ArgumentNullException> (() => client.SetLanguage (null));
				Assert.Throws<ArgumentException> (() => client.SetLanguage (string.Empty));

				Assert.Throws<AuthenticationException> (() => client.Authenticate ("username", "password"));
				Assert.IsTrue (client.IsConnected, "AuthenticationException should not cause a disconnect.");

				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageSizes ());
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageSize (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageUids ());
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageUid (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessage (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessages (0, 1));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessages (new int[] { 0 }));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageHeaders (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageHeaders (0, 1));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetMessageHeaders (new int[] { 0 }));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetStream (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetStreams (0, 1));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.GetStreams (new int[] { 0 }));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.DeleteMessage (0));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.DeleteMessages (0, 1));
				Assert.Throws<ServiceNotAuthenticatedException> (() => client.DeleteMessages (new int[] { 0 }));
				Assert.IsTrue (client.IsConnected, "ServiceNotAuthenticatedException should not cause a disconnect.");

				client.Authenticate (Encoding.UTF8, "username", "password");
				Assert.IsTrue (client.IsAuthenticated, "IsAuthenticated");

				Assert.Throws<InvalidOperationException> (() => client.Authenticate ("username", "password"));
				Assert.Throws<InvalidOperationException> (() => client.Authenticate (new NetworkCredential ("username", "password")));
				Assert.Throws<InvalidOperationException> (() => client.Authenticate (new SaslMechanismPlain ("username", "password")));

				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessageSize (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessageUid (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessage (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessages (-1, 1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessages (0, -1));
				Assert.Throws<ArgumentNullException> (() => client.GetMessages (null));
				Assert.Throws<ArgumentException> (() => client.GetMessages (new int[] { -1 }));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessageHeaders (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessageHeaders (-1, 1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetMessageHeaders (0, -1));
				Assert.Throws<ArgumentNullException> (() => client.GetMessageHeaders (null));
				Assert.Throws<ArgumentException> (() => client.GetMessageHeaders (new int[] { -1 }));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetStream (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetStreams (-1, 1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.GetStreams (0, -1));
				Assert.Throws<ArgumentNullException> (() => client.GetStreams (null));
				Assert.Throws<ArgumentException> (() => client.GetStreams (new int[] { -1 }));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.DeleteMessage (-1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.DeleteMessages (-1, 1));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.DeleteMessages (0, -1));
				Assert.Throws<ArgumentNullException> (() => client.DeleteMessages (null));
				Assert.Throws<ArgumentException> (() => client.DeleteMessages (new int[] { -1 }));

				Assert.AreEqual (0, client.GetStreams (0, 0).Count);
				Assert.AreEqual (0, client.GetStreams (new int[0]).Count);
				Assert.AreEqual (0, client.GetMessages (0, 0).Count);
				Assert.AreEqual (0, client.GetMessages (new int[0]).Count);
				Assert.AreEqual (0, client.GetMessageHeaders (0, 0).Count);
				Assert.AreEqual (0, client.GetMessageHeaders (new int[0]).Count);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestInvalidStateExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.AuthenticateAsync ("username", "password"));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.AuthenticateAsync (new NetworkCredential ("username", "password")));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.AuthenticateAsync (new SaslMechanismPlain ("username", "password")));

				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.EnableUTF8Async ());
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetLanguagesAsync ());
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.SetLanguageAsync ("en"));

				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.NoOpAsync ());

				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageSizesAsync ());
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageSizeAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageUidsAsync ());
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageUidAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessagesAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessagesAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageHeadersAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageHeadersAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetMessageHeadersAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetStreamAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetStreamsAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.GetStreamsAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.DeleteMessageAsync (0));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.DeleteMessagesAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotConnectedException> (async () => await client.DeleteMessagesAsync (new int[] { 0 }));

				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);
				Assert.AreEqual (0, client.LoginDelay);

				Assert.Throws<ArgumentException> (() => client.Capabilities |= Pop3Capabilities.Apop);
				Assert.DoesNotThrow (() => client.Capabilities &= ~Pop3Capabilities.UIDL);

				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.SetLanguageAsync (null));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.SetLanguageAsync (string.Empty));

				Assert.ThrowsAsync<AuthenticationException> (async () => await client.AuthenticateAsync ("username", "password"));
				Assert.IsTrue (client.IsConnected, "AuthenticationException should not cause a disconnect.");

				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageSizesAsync ());
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageSizeAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageUidsAsync ());
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageUidAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessagesAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessagesAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageHeadersAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageHeadersAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetMessageHeadersAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetStreamAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetStreamsAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.GetStreamsAsync (new int[] { 0 }));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.DeleteMessageAsync (0));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.DeleteMessagesAsync (0, 1));
				Assert.ThrowsAsync<ServiceNotAuthenticatedException> (async () => await client.DeleteMessagesAsync (new int[] { 0 }));
				Assert.IsTrue (client.IsConnected, "ServiceNotAuthenticatedException should not cause a disconnect.");

				await client.AuthenticateAsync (Encoding.UTF8, "username", "password");
				Assert.IsTrue (client.IsAuthenticated, "IsAuthenticated");

				Assert.ThrowsAsync<InvalidOperationException> (async () => await client.AuthenticateAsync ("username", "password"));
				Assert.ThrowsAsync<InvalidOperationException> (async () => await client.AuthenticateAsync (new NetworkCredential ("username", "password")));
				Assert.ThrowsAsync<InvalidOperationException> (async () => await client.AuthenticateAsync (new SaslMechanismPlain ("username", "password")));

				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageSizeAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageUidAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessagesAsync (-1, 1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessagesAsync (0, -1));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.GetMessagesAsync (null));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.GetMessagesAsync (new int[] { -1 }));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageHeadersAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageHeadersAsync (-1, 1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetMessageHeadersAsync (0, -1));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.GetMessageHeadersAsync (null));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.GetMessageHeadersAsync (new int[] { -1 }));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetStreamAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetStreamsAsync (-1, 1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.GetStreamsAsync (0, -1));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.GetStreamsAsync (null));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.GetStreamsAsync (new int[] { -1 }));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.DeleteMessageAsync (-1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.DeleteMessagesAsync (-1, 1));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.DeleteMessagesAsync (0, -1));
				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.DeleteMessagesAsync (null));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.DeleteMessagesAsync (new int[] { -1 }));

				Assert.AreEqual (0, (await client.GetStreamsAsync (0, 0)).Count);
				Assert.AreEqual (0, (await client.GetStreamsAsync (new int[0])).Count);
				Assert.AreEqual (0, (await client.GetMessagesAsync (0, 0)).Count);
				Assert.AreEqual (0, (await client.GetMessagesAsync (new int[0])).Count);
				Assert.AreEqual (0, (await client.GetMessageHeadersAsync (0, 0)).Count);
				Assert.AreEqual (0, (await client.GetMessageHeadersAsync (new int[0])).Count);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		void AssertGMailIsConnected (IMailService client)
		{
			Assert.IsTrue (client.IsConnected, "Expected the client to be connected");
			Assert.IsTrue (client.IsSecure, "Expected a secure connection");
			Assert.IsTrue (client.IsEncrypted, "Expected an encrypted connection");
			Assert.IsTrue (client.IsSigned, "Expected a signed connection");
			Assert.IsTrue (client.SslProtocol == SslProtocols.Tls12 || client.SslProtocol == SslProtocols.Tls13, "Expected a TLS v1.2 or TLS v1.3 connection");
			Assert.IsTrue (client.SslCipherAlgorithm == CipherAlgorithmType.Aes128 || client.SslCipherAlgorithm == CipherAlgorithmType.Aes256, "Unexpected SslCipherAlgorithm: {0}", client.SslCipherAlgorithm);
			Assert.IsTrue (client.SslCipherStrength == 128 || client.SslCipherStrength == 256, "Unexpected SslCipherStrength: {0}", client.SslCipherStrength);
			Assert.IsTrue (client.SslHashAlgorithm == HashAlgorithmType.Sha256 || client.SslHashAlgorithm == HashAlgorithmType.Sha384, "Unexpected SslHashAlgorithm: {0}", client.SslHashAlgorithm);
			Assert.AreEqual (0, client.SslHashStrength, "Unexpected SslHashStrength: {0}", client.SslHashStrength);
			Assert.IsTrue (client.SslKeyExchangeAlgorithm == ExchangeAlgorithmType.None || client.SslKeyExchangeAlgorithm == EcdhEphemeral, "Unexpected SslKeyExchangeAlgorithm: {0}", client.SslKeyExchangeAlgorithm);
			Assert.IsTrue (client.SslKeyExchangeStrength == 0 || client.SslKeyExchangeStrength == 255, "Unexpected SslKeyExchangeStrength: {0}", client.SslKeyExchangeStrength);
			Assert.IsFalse (client.IsAuthenticated, "Expected the client to not be authenticated");
		}

		void AssertClientIsDisconnected (IMailService client)
		{
			Assert.IsFalse (client.IsConnected, "Expected the client to be disconnected");
			Assert.IsFalse (client.IsSecure, "Expected IsSecure to be false after disconnecting");
			Assert.IsFalse (client.IsEncrypted, "Expected IsEncrypted to be false after disconnecting");
			Assert.IsFalse (client.IsSigned, "Expected IsSigned to be false after disconnecting");
			Assert.AreEqual (SslProtocols.None, client.SslProtocol, "Expected SslProtocol to be None after disconnecting");
			Assert.IsNull (client.SslCipherAlgorithm, "Expected SslCipherAlgorithm to be null after disconnecting");
			Assert.IsNull (client.SslCipherStrength, "Expected SslCipherStrength to be null after disconnecting");
			Assert.IsNull (client.SslHashAlgorithm, "Expected SslHashAlgorithm to be null after disconnecting");
			Assert.IsNull (client.SslHashStrength, "Expected SslHashStrength to be null after disconnecting");
			Assert.IsNull (client.SslKeyExchangeAlgorithm, "Expected SslKeyExchangeAlgorithm to be null after disconnecting");
			Assert.IsNull (client.SslKeyExchangeStrength, "Expected SslKeyExchangeStrength to be null after disconnecting");
		}

		[Test]
		public void TestConnectGMail ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var client = new Pop3Client ()) {
				int connected = 0, disconnected = 0;

				client.Connected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
					connected++;
				};

				client.Disconnected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
					Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
					disconnected++;
				};

				client.Connect (host, 0, options);
				AssertGMailIsConnected (client);
				Assert.AreEqual (1, connected, "ConnectedEvent");

				Assert.Throws<InvalidOperationException> (() => client.Connect (host, 0, options));

				client.Disconnect (true);
				AssertClientIsDisconnected (client);
				Assert.AreEqual (1, disconnected, "DisconnectedEvent");
			}
		}

		[Test]
		public async Task TestConnectGMailAsync ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var client = new Pop3Client ()) {
				int connected = 0, disconnected = 0;

				client.Connected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
					connected++;
				};

				client.Disconnected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
					Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
					disconnected++;
				};

				await client.ConnectAsync (host, 0, options);
				AssertGMailIsConnected (client);
				Assert.AreEqual (1, connected, "ConnectedEvent");

				Assert.ThrowsAsync<InvalidOperationException> (async () => await client.ConnectAsync (host, 0, options));

				await client.DisconnectAsync (true);
				AssertClientIsDisconnected (client);
				Assert.AreEqual (1, disconnected, "DisconnectedEvent");
			}
		}

		[Test]
		public void TestConnectGMailViaProxy ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var proxy = new Socks5ProxyListener ()) {
				proxy.Start (IPAddress.Loopback, 0);

				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					client.ProxyClient = new Socks5Client (proxy.IPAddress.ToString (), proxy.Port);
					client.ServerCertificateValidationCallback = (s, c, h, e) => true;
					client.ClientCertificates = null;
					client.LocalEndPoint = null;
					client.Timeout = 20000;

					try {
						client.Connect (host, 0, options);
					} catch (TimeoutException) {
						Assert.Inconclusive ("Timed out.");
						return;
					} catch (Exception ex) {
						Assert.Fail (ex.Message);
					}
					AssertGMailIsConnected (client);
					Assert.AreEqual (1, connected, "ConnectedEvent");

					Assert.Throws<InvalidOperationException> (() => client.Connect (host, 0, options));

					client.Disconnect (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public async Task TestConnectGMailViaProxyAsync ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var proxy = new Socks5ProxyListener ()) {
				proxy.Start (IPAddress.Loopback, 0);

				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					client.ProxyClient = new Socks5Client (proxy.IPAddress.ToString (), proxy.Port);
					client.ServerCertificateValidationCallback = (s, c, h, e) => true;
					client.ClientCertificates = null;
					client.LocalEndPoint = null;
					client.Timeout = 20000;

					try {
						await client.ConnectAsync (host, 0, options);
					} catch (TimeoutException) {
						Assert.Inconclusive ("Timed out.");
						return;
					} catch (Exception ex) {
						Assert.Fail (ex.Message);
					}
					AssertGMailIsConnected (client);
					Assert.AreEqual (1, connected, "ConnectedEvent");

					Assert.ThrowsAsync<InvalidOperationException> (async () => await client.ConnectAsync (host, 0, options));

					await client.DisconnectAsync (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public void TestConnectGMailSocket ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var client = new Pop3Client ()) {
				int connected = 0, disconnected = 0;

				client.Connected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
					connected++;
				};

				client.Disconnected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
					Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
					disconnected++;
				};

				var socket = Connect (host, port);

				Assert.Throws<ArgumentNullException> (() => client.Connect (socket, null, port, SecureSocketOptions.Auto));
				Assert.Throws<ArgumentException> (() => client.Connect (socket, "", port, SecureSocketOptions.Auto));
				Assert.Throws<ArgumentOutOfRangeException> (() => client.Connect (socket, host, -1, SecureSocketOptions.Auto));

				client.Connect (socket, host, port, SecureSocketOptions.Auto);
				AssertGMailIsConnected (client);
				Assert.AreEqual (1, connected, "ConnectedEvent");

				Assert.Throws<InvalidOperationException> (() => client.Connect (socket, "pop.gmail.com", 995, SecureSocketOptions.Auto));

				client.Disconnect (true);
				AssertClientIsDisconnected (client);
				Assert.AreEqual (1, disconnected, "DisconnectedEvent");
			}
		}

		[Test]
		public async Task TestConnectGMailSocketAsync ()
		{
			var options = SecureSocketOptions.SslOnConnect;
			var host = "pop.gmail.com";
			int port = 995;

			using (var client = new Pop3Client ()) {
				int connected = 0, disconnected = 0;

				client.Connected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
					connected++;
				};

				client.Disconnected += (sender, e) => {
					Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
					Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
					Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
					Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
					disconnected++;
				};

				var socket = Connect (host, port);

				Assert.ThrowsAsync<ArgumentNullException> (async () => await client.ConnectAsync (socket, null, port, SecureSocketOptions.Auto));
				Assert.ThrowsAsync<ArgumentException> (async () => await client.ConnectAsync (socket, "", port, SecureSocketOptions.Auto));
				Assert.ThrowsAsync<ArgumentOutOfRangeException> (async () => await client.ConnectAsync (socket, host, -1, SecureSocketOptions.Auto));

				await client.ConnectAsync (socket, host, port, SecureSocketOptions.Auto);
				AssertGMailIsConnected (client);
				Assert.AreEqual (1, connected, "ConnectedEvent");

				Assert.ThrowsAsync<InvalidOperationException> (async () => await client.ConnectAsync (socket, host, port, SecureSocketOptions.Auto));

				await client.DisconnectAsync (true);
				AssertClientIsDisconnected (client);
				Assert.AreEqual (1, disconnected, "DisconnectedEvent");
			}
		}

		[Test]
		public void TestConnectGmxDe ()
		{
			var options = SecureSocketOptions.StartTls;
			var host = "pop.gmx.de";
			var port = 110;

			using (var cancel = new CancellationTokenSource (30 * 1000)) {
				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					var uri = new Uri ($"pop://{host}/?starttls=always");
					client.Connect (uri, cancel.Token);
					Assert.IsTrue (client.IsConnected, "Expected the client to be connected");
					Assert.IsTrue (client.IsSecure, "Expected a secure connection");
					Assert.IsTrue (client.IsEncrypted, "Expected an encrypted connection");
					Assert.IsTrue (client.IsSigned, "Expected a signed connection");
					Assert.IsTrue (client.SslProtocol == SslProtocols.Tls12 || client.SslProtocol == SslProtocols.Tls13, "Expected a TLS v1.2 or TLS v1.3 connection");
					Assert.AreEqual (GmxDeCipherAlgorithm, client.SslCipherAlgorithm);
					Assert.AreEqual (GmxDeCipherStrength, client.SslCipherStrength);
					Assert.AreEqual (GmxDeHashAlgorithm, client.SslHashAlgorithm);
					Assert.AreEqual (0, client.SslHashStrength, "Unexpected SslHashStrength: {0}", client.SslHashStrength);
					Assert.IsTrue (client.SslKeyExchangeAlgorithm == ExchangeAlgorithmType.None || client.SslKeyExchangeAlgorithm == EcdhEphemeral, "Unexpected SslKeyExchangeAlgorithm: {0}", client.SslKeyExchangeAlgorithm);
					Assert.IsTrue (client.SslKeyExchangeStrength == 0 || client.SslKeyExchangeStrength == 255, "Unexpected SslKeyExchangeStrength: {0}", client.SslKeyExchangeStrength);
					Assert.IsFalse (client.IsAuthenticated, "Expected the client to not be authenticated");
					Assert.AreEqual (1, connected, "ConnectedEvent");

					client.Disconnect (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public async Task TestConnectGmxDeAsync ()
		{
			var options = SecureSocketOptions.StartTls;
			var host = "pop.gmx.de";
			var port = 110;

			using (var cancel = new CancellationTokenSource (30 * 1000)) {
				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					var uri = new Uri ($"pop://{host}/?starttls=always");
					await client.ConnectAsync (uri, cancel.Token);
					Assert.IsTrue (client.IsConnected, "Expected the client to be connected");
					Assert.IsTrue (client.IsSecure, "Expected a secure connection");
					Assert.IsTrue (client.IsEncrypted, "Expected an encrypted connection");
					Assert.IsTrue (client.IsSigned, "Expected a signed connection");
					Assert.IsTrue (client.SslProtocol == SslProtocols.Tls12 || client.SslProtocol == SslProtocols.Tls13, "Expected a TLS v1.2 or TLS v1.3 connection");
					Assert.AreEqual (GmxDeCipherAlgorithm, client.SslCipherAlgorithm);
					Assert.AreEqual (GmxDeCipherStrength, client.SslCipherStrength);
					Assert.AreEqual (GmxDeHashAlgorithm, client.SslHashAlgorithm);
					Assert.AreEqual (0, client.SslHashStrength, "Unexpected SslHashStrength: {0}", client.SslHashStrength);
					Assert.IsTrue (client.SslKeyExchangeAlgorithm == ExchangeAlgorithmType.None || client.SslKeyExchangeAlgorithm == EcdhEphemeral, "Unexpected SslKeyExchangeAlgorithm: {0}", client.SslKeyExchangeAlgorithm);
					Assert.IsTrue (client.SslKeyExchangeStrength == 0 || client.SslKeyExchangeStrength == 255, "Unexpected SslKeyExchangeStrength: {0}", client.SslKeyExchangeStrength);
					Assert.IsFalse (client.IsAuthenticated, "Expected the client to not be authenticated");
					Assert.AreEqual (1, connected, "ConnectedEvent");

					await client.DisconnectAsync (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public void TestConnectGmxDeSocket ()
		{
			var options = SecureSocketOptions.StartTls;
			var host = "pop.gmx.de";
			var port = 110;

			using (var cancel = new CancellationTokenSource (30 * 1000)) {
				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					var socket = Connect (host, port);
					client.Connect (socket, host, port, options, cancel.Token);
					Assert.IsTrue (client.IsConnected, "Expected the client to be connected");
					Assert.IsTrue (client.IsSecure, "Expected a secure connection");
					Assert.IsTrue (client.IsEncrypted, "Expected an encrypted connection");
					Assert.IsTrue (client.IsSigned, "Expected a signed connection");
					Assert.IsTrue (client.SslProtocol == SslProtocols.Tls12 || client.SslProtocol == SslProtocols.Tls13, "Expected a TLS v1.2 or TLS v1.3 connection");
					Assert.AreEqual (GmxDeCipherAlgorithm, client.SslCipherAlgorithm);
					Assert.AreEqual (GmxDeCipherStrength, client.SslCipherStrength);
					Assert.AreEqual (GmxDeHashAlgorithm, client.SslHashAlgorithm);
					Assert.AreEqual (0, client.SslHashStrength, "Unexpected SslHashStrength: {0}", client.SslHashStrength);
					Assert.IsTrue (client.SslKeyExchangeAlgorithm == ExchangeAlgorithmType.None || client.SslKeyExchangeAlgorithm == EcdhEphemeral, "Unexpected SslKeyExchangeAlgorithm: {0}", client.SslKeyExchangeAlgorithm);
					Assert.IsTrue (client.SslKeyExchangeStrength == 0 || client.SslKeyExchangeStrength == 255, "Unexpected SslKeyExchangeStrength: {0}", client.SslKeyExchangeStrength);
					Assert.IsFalse (client.IsAuthenticated, "Expected the client to not be authenticated");
					Assert.AreEqual (1, connected, "ConnectedEvent");

					client.Disconnect (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public async Task TestConnectGmxDeSocketAsync ()
		{
			var options = SecureSocketOptions.StartTls;
			var host = "pop.gmx.de";
			var port = 110;

			using (var cancel = new CancellationTokenSource (30 * 1000)) {
				using (var client = new Pop3Client ()) {
					int connected = 0, disconnected = 0;

					client.Connected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "ConnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "ConnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "ConnectedEventArgs.Options");
						connected++;
					};

					client.Disconnected += (sender, e) => {
						Assert.AreEqual (host, e.Host, "DisconnectedEventArgs.Host");
						Assert.AreEqual (port, e.Port, "DisconnectedEventArgs.Port");
						Assert.AreEqual (options, e.Options, "DisconnectedEventArgs.Options");
						Assert.IsTrue (e.IsRequested, "DisconnectedEventArgs.IsRequested");
						disconnected++;
					};

					var socket = Connect (host, port);
					await client.ConnectAsync (socket, host, port, options, cancel.Token);
					Assert.IsTrue (client.IsConnected, "Expected the client to be connected");
					Assert.IsTrue (client.IsSecure, "Expected a secure connection");
					Assert.IsTrue (client.IsEncrypted, "Expected an encrypted connection");
					Assert.IsTrue (client.IsSigned, "Expected a signed connection");
					Assert.IsTrue (client.SslProtocol == SslProtocols.Tls12 || client.SslProtocol == SslProtocols.Tls13, "Expected a TLS v1.2 or TLS v1.3 connection");
					Assert.AreEqual (GmxDeCipherAlgorithm, client.SslCipherAlgorithm);
					Assert.AreEqual (GmxDeCipherStrength, client.SslCipherStrength);
					Assert.AreEqual (GmxDeHashAlgorithm, client.SslHashAlgorithm);
					Assert.AreEqual (0, client.SslHashStrength, "Unexpected SslHashStrength: {0}", client.SslHashStrength);
					Assert.IsTrue (client.SslKeyExchangeAlgorithm == ExchangeAlgorithmType.None || client.SslKeyExchangeAlgorithm == EcdhEphemeral, "Unexpected SslKeyExchangeAlgorithm: {0}", client.SslKeyExchangeAlgorithm);
					Assert.IsTrue (client.SslKeyExchangeStrength == 0 || client.SslKeyExchangeStrength == 255, "Unexpected SslKeyExchangeStrength: {0}", client.SslKeyExchangeStrength);
					Assert.IsFalse (client.IsAuthenticated, "Expected the client to not be authenticated");
					Assert.AreEqual (1, connected, "ConnectedEvent");

					await client.DisconnectAsync (true);
					AssertClientIsDisconnected (client);
					Assert.AreEqual (1, disconnected, "DisconnectedEvent");
				}
			}
		}

		[Test]
		public void TestGreetingOk ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "common.ok-greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");
				Assert.IsFalse (client.IsSecure, "IsSecure should be false.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy, "ExpirePolicy");
				Assert.AreEqual (120000, client.Timeout, "Timeout");

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGreetingOkAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "common.ok-greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");
				Assert.IsFalse (client.IsSecure, "IsSecure should be false.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy, "ExpirePolicy");
				Assert.AreEqual (120000, client.Timeout, "Timeout");

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGreetingErr ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "common.err-greeting.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					Assert.Fail ("Expected Connect to fail.");
				} catch (Pop3ProtocolException) {
					Assert.Pass ();
				} catch (Exception ex) {
					Assert.Fail ("Expected Pop3ProtocolException from Connect: {0}", ex);
				}
			}
		}

		[Test]
		public async Task TestGreetingErrAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "common.err-greeting.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					Assert.Fail ("Expected Connect to fail.");
				} catch (Pop3ProtocolException) {
					Assert.Pass ();
				} catch (Exception ex) {
					Assert.Fail ("Expected Pop3ProtocolException from Connect: {0}", ex);
				}
			}
		}

		[Test]
		public void TestBasicPop3Client ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "comcast.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");
				Assert.IsFalse (client.IsSecure, "IsSecure should be false.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy, "ExpirePolicy");
				Assert.AreEqual (120000, client.Timeout, "Timeout");
				client.Timeout *= 2;

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var sizes = client.GetMessageSizes ();
					Assert.AreEqual (7, sizes.Count, "Expected 7 message sizes");
					for (int i = 0; i < sizes.Count; i++)
						Assert.AreEqual ((i + 1) * 1024, sizes[i], "Unexpected size for message #{0}", i);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					var size = client.GetMessageSize (0);
					Assert.AreEqual (1024, size, "Unexpected size for 1st message");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					using (var message = client.GetMessage (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var count = client.GetMessageCount ();
					Assert.AreEqual (7, count, "Expected 7 messages again");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageCount: {0}", ex);
				}

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestBasicPop3ClientAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "comcast.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");
				Assert.IsFalse (client.IsSecure, "IsSecure should be false.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy, "ExpirePolicy");
				Assert.AreEqual (120000, client.Timeout, "Timeout");
				client.Timeout *= 2;

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var sizes = await client.GetMessageSizesAsync ();
					Assert.AreEqual (7, sizes.Count, "Expected 7 message sizes");
					for (int i = 0; i < sizes.Count; i++)
						Assert.AreEqual ((i + 1) * 1024, sizes[i], "Unexpected size for message #{0}", i);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					var size = await client.GetMessageSizeAsync (0);
					Assert.AreEqual (1024, size, "Unexpected size for 1st message");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					using (var message = await client.GetMessageAsync (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var count = await client.GetMessageCountAsync ();
					Assert.AreEqual (7, count, "Expected 7 messages again");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageCount: {0}", ex);
				}

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestBasicPop3ClientUnixLineEndings ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "comcast.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, true));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var sizes = client.GetMessageSizes ();
					Assert.AreEqual (7, sizes.Count, "Expected 7 message sizes");
					for (int i = 0; i < sizes.Count; i++)
						Assert.AreEqual ((i + 1) * 1024, sizes[i], "Unexpected size for message #{0}", i);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					var size = client.GetMessageSize (0);
					Assert.AreEqual (1024, size, "Unexpected size for 1st message");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					using (var message = client.GetMessage (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var count = client.GetMessageCount ();
					Assert.AreEqual (7, count, "Expected 7 messages again");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageCount: {0}", ex);
				}

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestBasicPop3ClientUnixLineEndingsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "comcast.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, true));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var sizes = await client.GetMessageSizesAsync ();
					Assert.AreEqual (7, sizes.Count, "Expected 7 message sizes");
					for (int i = 0; i < sizes.Count; i++)
						Assert.AreEqual ((i + 1) * 1024, sizes[i], "Unexpected size for message #{0}", i);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					var size = await client.GetMessageSizeAsync (0);
					Assert.AreEqual (1024, size, "Unexpected size for 1st message");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageSizes: {0}", ex);
				}

				try {
					using (var message = await client.GetMessageAsync (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var count = await client.GetMessageCountAsync ();
					Assert.AreEqual (7, count, "Expected 7 messages again");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageCount: {0}", ex);
				}

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestProbedUidlSupport ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (Pop3Capabilities.User | Pop3Capabilities.UIDL, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				client.GetMessageUids ();

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestProbedUidlSupportAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (Pop3Capabilities.User | Pop3Capabilities.UIDL, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				await client.GetMessageUidsAsync ();

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestProbedUidlSupportError ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.Throws<NotSupportedException> (() => client.GetMessageUids ());

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestProbedUidlSupportErrorAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "comcast.err.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (Pop3Capabilities.User, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (0, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.ThrowsAsync<NotSupportedException> (() => client.GetMessageUidsAsync ());

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestEnableUTF8 ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "lang.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						client.EnableUTF8 ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
					}

					// Try to enable UTF8 again even though we've done it. This should just no-op and not send another command.
					try {
						client.EnableUTF8 ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception from second call to EnableUTF8: {0}", ex);
					}

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public async Task TestEnableUTF8Async ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "lang.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						await client.EnableUTF8Async ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
					}

					// Try to enable UTF8 again even though we've done it. This should just no-op and not send another command.
					try {
						await client.EnableUTF8Async ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception from second call to EnableUTF8: {0}", ex);
					}

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public void TestEnableUTF8AfterAuthenticate ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "lang.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						client.Authenticate ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (3, client.Count, "Expected 3 messages");
					Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

					try {
						client.EnableUTF8 ();
						Assert.Fail ("EnableUTF8() should throw InvalidOperationException.");
					} catch (InvalidOperationException) {
						Assert.Pass ();
					} catch (Exception ex) {
						Assert.Fail ("Unexpected exception thrown by EnableUTF8: {0}", ex);
					}

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public async Task TestEnableUTF8AfterAuthenticateAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "lang.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						await client.AuthenticateAsync ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (3, client.Count, "Expected 3 messages");
					Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

					try {
						await client.EnableUTF8Async ();
						Assert.Fail ("EnableUTF8Async() should throw InvalidOperationException.");
					} catch (InvalidOperationException) {
						Assert.Pass ();
					} catch (Exception ex) {
						Assert.Fail ("Unexpected exception thrown by EnableUTF8Async: {0}", ex);
					}

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public void TestEnableUTF8NotSupported ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (GMailCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						client.EnableUTF8 ();
						Assert.Fail ("EnableUTF8() should throw NotSupportedException.");
					} catch (NotSupportedException) {
						Assert.Pass ();
					} catch (Exception ex) {
						Assert.Fail ("Unexpected exception thrown by EnableUTF8: {0}", ex);
					}

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public async Task TestEnableUTF8NotSupportedAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client ()) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (GMailCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						await client.EnableUTF8Async ();
						Assert.Fail ("EnableUTF8Async() should throw NotSupportedException.");
					} catch (NotSupportedException) {
						Assert.Pass ();
					} catch (Exception ex) {
						Assert.Fail ("Unexpected exception thrown by EnableUTF8Async: {0}", ex);
					}

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}
			}
		}

		[Test]
		public void TestGetMessageCountParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageCount ());
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageCount ());
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageCount ());
				Assert.IsTrue (client.IsConnected);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGetMessageCountParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageCountAsync ());
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageCountAsync ());
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageCountAsync ());
				Assert.IsTrue (client.IsConnected);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGetMessageSizeParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSize (0));
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSize (0));
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSize (0));
				Assert.IsTrue (client.IsConnected);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGetMessageSizeParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "comcast.list1-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizeAsync (0));
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizeAsync (0));
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizeAsync (0));
				Assert.IsTrue (client.IsConnected);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGetMessageSizesParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSizes ());
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSizes ());
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageSizes ());
				Assert.IsTrue (client.IsConnected);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGetMessageSizesParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "comcast.list-error3.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ComcastCapa1, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (31, client.ExpirePolicy);

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ComcastCapa2, client.Capabilities);
				Assert.AreEqual ("ZimbraInc", client.Implementation);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
				Assert.AreEqual (-1, client.ExpirePolicy);

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizesAsync ());
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizesAsync ());
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageSizesAsync ());
				Assert.IsTrue (client.IsConnected);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGetMessageUidParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageUid (0));
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageUid (0));
				Assert.IsTrue (client.IsConnected);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGetMessageUidParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageUidAsync (0));
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageUidAsync (0));
				Assert.IsTrue (client.IsConnected);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGetMessageUidsParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageUids ());
				Assert.IsTrue (client.IsConnected);

				Assert.Throws<Pop3ProtocolException> (() => client.GetMessageUids ());
				Assert.IsTrue (client.IsConnected);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestGetMessageUidsParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl-error1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl-error2.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageUidsAsync ());
				Assert.IsTrue (client.IsConnected);

				Assert.ThrowsAsync<Pop3ProtocolException> (async () => await client.GetMessageUidsAsync ());
				Assert.IsTrue (client.IsConnected);

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestSaslAuthentication ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				try {
					var credentials = new NetworkCredential ("username", "password");
					var sasl = new SaslMechanismLogin (credentials);

					client.Authenticate (sasl);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestSaslAuthenticationAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				try {
					var credentials = new NetworkCredential ("username", "password");
					var sasl = new SaslMechanismLogin (credentials);

					await client.AuthenticateAsync (sasl);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		static void AssertRedacted (MemoryStream stream, string commandPrefix, string nextCommandPrefix)
		{
			stream.Position = 0;

			using (var reader = new StreamReader (stream, Encoding.ASCII, false, 1024, true)) {
				string secrets;
				string line;

				while ((line = reader.ReadLine ()) != null) {
					if (line.StartsWith (commandPrefix, StringComparison.Ordinal))
						break;
				}

				Assert.NotNull (line, "Authentication command not found: {0}", commandPrefix);

				if (line.Length > commandPrefix.Length) {
					secrets = line.Substring (commandPrefix.Length);

					var tokens = secrets.Split (' ');
					var expectedTokens = new string[tokens.Length];
					for (int i = 0; i < tokens.Length; i++)
						expectedTokens[i] = "********";

					var expected = string.Join (" ", expectedTokens);

					Assert.AreEqual (expected, secrets, commandPrefix);
				}

				while ((line = reader.ReadLine ()) != null) {
					if (line.StartsWith (nextCommandPrefix, StringComparison.Ordinal))
						return;

					if (!line.StartsWith ("C: ", StringComparison.Ordinal))
						continue;

					secrets = line.Substring (3);

					Assert.AreEqual ("********", secrets, "SASL challenge");
				}

				Assert.Fail ("Did not find response.");
			}
		}

		[Test]
		public void TestRedactApop ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						client.EnableUTF8 ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
					}

					try {
						client.Authenticate ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (LangCapa2, client.Capabilities);
					Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

					Assert.AreEqual (3, client.Count, "Expected 3 messages");
					Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: APOP ", "C: CAPA");
			}
		}

		[Test]
		public async Task TestRedactApopAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (LangCapa1, client.Capabilities);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

					try {
						await client.EnableUTF8Async ();
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
					}

					try {
						await client.AuthenticateAsync ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (LangCapa2, client.Capabilities);
					Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

					Assert.AreEqual (3, client.Count, "Expected 3 messages");
					Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: APOP ", "C: CAPA");
			}
		}

		[Test]
		public void TestRedactAuthentication ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					client.AuthenticationMechanisms.Remove ("GSSAPI");
					client.AuthenticationMechanisms.Remove ("NTLM");
					client.AuthenticationMechanisms.Remove ("PLAIN");

					try {
						client.Authenticate ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: AUTH LOGIN", "C: CAPA");
			}
		}

		[Test]
		public async Task TestRedactAuthenticationAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					client.AuthenticationMechanisms.Remove ("GSSAPI");
					client.AuthenticationMechanisms.Remove ("NTLM");
					client.AuthenticationMechanisms.Remove ("PLAIN");

					try {
						await client.AuthenticateAsync ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: AUTH LOGIN", "C: CAPA");
			}
		}

		[Test]
		public void TestRedactUserPass ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ComcastCapa1, client.Capabilities);
					Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
					Assert.AreEqual (31, client.ExpirePolicy);

					try {
						client.Authenticate ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ComcastCapa2, client.Capabilities);
					Assert.AreEqual ("ZimbraInc", client.Implementation);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
					Assert.AreEqual (-1, client.ExpirePolicy);

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: USER ", "C: PASS");
				AssertRedacted (stream, "C: PASS ", "C: CAPA");
			}
		}

		[Test]
		public async Task TestRedactUserPassAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "comcast.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("USER username\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("PASS password\r\n", "comcast.ok.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "comcast.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "comcast.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "comcast.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ComcastCapa1, client.Capabilities);
					Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
					Assert.AreEqual (31, client.ExpirePolicy);

					try {
						await client.AuthenticateAsync ("username", "password");
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ComcastCapa2, client.Capabilities);
					Assert.AreEqual ("ZimbraInc", client.Implementation);
					Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("X-ZIMBRA"), "Expected SASL X-ZIMBRA auth mechanism");
					Assert.AreEqual (-1, client.ExpirePolicy);

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: USER ", "C: PASS");
				AssertRedacted (stream, "C: PASS ", "C: CAPA");
			}
		}

		[Test]
		public void TestRedactSaslAuthentication ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					try {
						var credentials = new NetworkCredential ("username", "password");
						var sasl = new SaslMechanismLogin (credentials);

						client.Authenticate (sasl);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						client.Disconnect (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: AUTH LOGIN", "C: CAPA");
			}
		}

		[Test]
		public async Task TestRedactSaslAuthenticationAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var stream = new MemoryStream ()) {
				using (var client = new Pop3Client (new ProtocolLogger (stream, true) { RedactSecrets = true })) {
					try {
						await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
					}

					Assert.IsTrue (client.IsConnected, "Client failed to connect.");

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					try {
						var credentials = new NetworkCredential ("username", "password");
						var sasl = new SaslMechanismLogin (credentials);

						await client.AuthenticateAsync (sasl);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
					}

					Assert.AreEqual (ExchangeCapa, client.Capabilities);
					Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
					Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

					Assert.AreEqual (7, client.Count, "Expected 7 messages");
					Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

					try {
						await client.DisconnectAsync (true);
					} catch (Exception ex) {
						Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
					}

					Assert.IsFalse (client.IsConnected, "Failed to disconnect");
				}

				AssertRedacted (stream, "C: AUTH LOGIN", "C: CAPA");
			}
		}

		[Test]
		public void TestExchangePop3Client ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "exchange.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "exchange.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				// Note: remove these auth mechanisms to force LOGIN auth
				client.AuthenticationMechanisms.Remove ("GSSAPI");
				client.AuthenticationMechanisms.Remove ("NTLM");
				client.AuthenticationMechanisms.Remove ("PLAIN");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var uids = client.GetMessageUids ();
					Assert.AreEqual (7, uids.Count, "Expected 7 uids");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageUids: {0}", ex);
				}

				try {
					using (var message = client.GetMessage (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestExchangePop3ClientAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "exchange.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH LOGIN\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("dXNlcm5hbWU=\r\n", "exchange.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("cGFzc3dvcmQ=\r\n", "exchange.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "exchange.capa.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "exchange.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "exchange.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "exchange.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "exchange.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				// Note: remove these auth mechanisms to force LOGIN auth
				client.AuthenticationMechanisms.Remove ("GSSAPI");
				client.AuthenticationMechanisms.Remove ("NTLM");
				client.AuthenticationMechanisms.Remove ("PLAIN");

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (ExchangeCapa, client.Capabilities);
				Assert.AreEqual (4, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("GSSAPI"), "Expected SASL GSSAPI auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("NTLM"), "Expected SASL NTLM auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("LOGIN"), "Expected SASL LOGIN auth mechanism");

				Assert.AreEqual (7, client.Count, "Expected 7 messages");
				Assert.AreEqual (1800662, client.Size, "Expected 1800662 octets");

				try {
					var uids = await client.GetMessageUidsAsync ();
					Assert.AreEqual (7, uids.Count, "Expected 7 uids");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageUids: {0}", ex);
				}

				try {
					using (var message = await client.GetMessageAsync (0)) {
						// TODO: assert that the message is byte-identical to what we expect
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		List<Pop3ReplayCommand> CreateGMailCommands ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 2\r\n", "gmail.uidl2.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 3\r\n", "gmail.uidl3.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "gmail.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "gmail.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 2\r\n", "gmail.list2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 3\r\n", "gmail.list3.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\nRETR 2\r\nRETR 3\r\n", "gmail.retr123.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\nRETR 2\r\nRETR 3\r\n", "gmail.retr123.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\nTOP 2 0\r\nTOP 3 0\r\n", "gmail.top123.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\nTOP 2 0\r\nTOP 3 0\r\n", "gmail.top123.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\nRETR 2\r\nRETR 3\r\n", "gmail.retr123.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\nRETR 2\r\nRETR 3\r\n", "gmail.retr123.txt"));
			commands.Add (new Pop3ReplayCommand ("NOOP\r\n", "gmail.noop.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\nDELE 2\r\nDELE 3\r\n", "gmail.dele123.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\nDELE 2\r\nDELE 3\r\n", "gmail.dele123.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\nDELE 2\r\nDELE 3\r\n", "gmail.dele123.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			return commands;
		}

		void TestGMailPop3Client (List<Pop3ReplayCommand> commands, bool disablePipelining)
		{
			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				if (disablePipelining)
					client.Capabilities &= ~Pop3Capabilities.Pipelining;

				var uids = client.GetMessageUids ();
				Assert.AreEqual (3, uids.Count);
				Assert.AreEqual ("101", uids[0]);
				Assert.AreEqual ("102", uids[1]);
				Assert.AreEqual ("103", uids[2]);

				for (int i = 0; i < 3; i++) {
					var uid = client.GetMessageUid (i);

					Assert.AreEqual (uids[i], uid);
				}

				var sizes = client.GetMessageSizes ();
				Assert.AreEqual (3, sizes.Count);
				Assert.AreEqual (1024, sizes[0]);
				Assert.AreEqual (1025, sizes[1]);
				Assert.AreEqual (1026, sizes[2]);

				for (int i = 0; i < 3; i++) {
					var size = client.GetMessageSize (i);

					Assert.AreEqual (sizes[i], size);
				}

				try {
					using (var message = client.GetMessage (0)) {
						using (var jpeg = new MemoryStream ()) {
							var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

							attachment.Content.DecodeTo (jpeg);
							jpeg.Position = 0;

							using (var md5 = MD5.Create ()) {
								var md5sum = HexEncode (md5.ComputeHash (jpeg));

								Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
							}
						}
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var messages = client.GetMessages (0, 3);

					foreach (var message in messages) {
						using (var jpeg = new MemoryStream ()) {
							var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

							attachment.Content.DecodeTo (jpeg);
							jpeg.Position = 0;

							using (var md5 = MD5.Create ()) {
								var md5sum = HexEncode (md5.ComputeHash (jpeg));

								Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
							}
						}

						message.Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessages: {0}", ex);
				}

				try {
					var messages = client.GetMessages (new [] { 0, 1, 2 });

					foreach (var message in messages) {
						using (var jpeg = new MemoryStream ()) {
							var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

							attachment.Content.DecodeTo (jpeg);
							jpeg.Position = 0;

							using (var md5 = MD5.Create ()) {
								var md5sum = HexEncode (md5.ComputeHash (jpeg));

								Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
							}
						}

						message.Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessages: {0}", ex);
				}

				try {
					var header = client.GetMessageHeaders (0);

					Assert.AreEqual ("Test inline image", header[HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					var headers = client.GetMessageHeaders (0, 3);

					Assert.AreEqual (3, headers.Count);
					for (int i = 0; i < headers.Count; i++)
						Assert.AreEqual ("Test inline image", headers[i][HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					var headers = client.GetMessageHeaders (new [] { 0, 1, 2 });

					Assert.AreEqual (3, headers.Count);
					for (int i = 0; i < headers.Count; i++)
						Assert.AreEqual ("Test inline image", headers[i][HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					using (var stream = client.GetStream (0)) {
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStream: {0}", ex);
				}

				try {
					var streams = client.GetStreams (0, 3);

					Assert.AreEqual (3, streams.Count);
					for (int i = 0; i < 3; i++) {
						streams[i].Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStreams: {0}", ex);
				}

				try {
					var streams = client.GetStreams (new int[] { 0, 1, 2 });

					Assert.AreEqual (3, streams.Count);
					for (int i = 0; i < 3; i++) {
						streams[i].Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStreams: {0}", ex);
				}

				try {
					client.NoOp ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in NoOp: {0}", ex);
				}

				try {
					client.DeleteMessage (0);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessage: {0}", ex);
				}

				try {
					client.Reset ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					client.DeleteMessages (new [] { 0, 1, 2 });
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessages: {0}", ex);
				}

				try {
					client.Reset ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					client.DeleteMessages (0, 3);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessages: {0}", ex);
				}

				try {
					client.Reset ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					client.DeleteAllMessages ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteAllMessages: {0}", ex);
				}

				try {
					client.Reset ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestGMailPop3Client ()
		{
			TestGMailPop3Client (CreateGMailCommands (), false);
		}

		async Task TestGMailPop3ClientAsync (List<Pop3ReplayCommand> commands, bool disablePipelining)
		{
			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				if (disablePipelining)
					client.Capabilities &= ~Pop3Capabilities.Pipelining;

				var uids = await client.GetMessageUidsAsync ();
				Assert.AreEqual (3, uids.Count);
				Assert.AreEqual ("101", uids[0]);
				Assert.AreEqual ("102", uids[1]);
				Assert.AreEqual ("103", uids[2]);

				for (int i = 0; i < 3; i++) {
					var uid = await client.GetMessageUidAsync (i);

					Assert.AreEqual (uids[i], uid);
				}

				var sizes = await client.GetMessageSizesAsync ();
				Assert.AreEqual (3, sizes.Count);
				Assert.AreEqual (1024, sizes[0]);
				Assert.AreEqual (1025, sizes[1]);
				Assert.AreEqual (1026, sizes[2]);

				for (int i = 0; i < 3; i++) {
					var size = await client.GetMessageSizeAsync (i);

					Assert.AreEqual (sizes[i], size);
				}

				try {
					var message = await client.GetMessageAsync (0);

					using (var jpeg = new MemoryStream ()) {
						var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

						attachment.Content.DecodeTo (jpeg);
						jpeg.Position = 0;

						using (var md5 = MD5.Create ()) {
							var md5sum = HexEncode (md5.ComputeHash (jpeg));

							Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
						}
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessage: {0}", ex);
				}

				try {
					var messages = await client.GetMessagesAsync (0, 3);

					foreach (var message in messages) {
						using (var jpeg = new MemoryStream ()) {
							var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

							attachment.Content.DecodeTo (jpeg);
							jpeg.Position = 0;

							using (var md5 = MD5.Create ()) {
								var md5sum = HexEncode (md5.ComputeHash (jpeg));

								Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
							}
						}

						message.Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessages: {0}", ex);
				}

				try {
					var messages = await client.GetMessagesAsync (new [] { 0, 1, 2 });

					foreach (var message in messages) {
						using (var jpeg = new MemoryStream ()) {
							var attachment = message.Attachments.OfType<MimePart> ().FirstOrDefault ();

							attachment.Content.DecodeTo (jpeg);
							jpeg.Position = 0;

							using (var md5 = MD5.Create ()) {
								var md5sum = HexEncode (md5.ComputeHash (jpeg));

								Assert.AreEqual ("5b1b8b2c9300c9cd01099f44e1155e2b", md5sum, "MD5 checksums do not match.");
							}
						}

						message.Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessages: {0}", ex);
				}

				try {
					var header = await client.GetMessageHeadersAsync (0);

					Assert.AreEqual ("Test inline image", header[HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					var headers = await client.GetMessageHeadersAsync (0, 3);

					Assert.AreEqual (3, headers.Count);
					for (int i = 0; i < headers.Count; i++)
						Assert.AreEqual ("Test inline image", headers[i][HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					var headers = await client.GetMessageHeadersAsync (new [] { 0, 1, 2 });

					Assert.AreEqual (3, headers.Count);
					for (int i = 0; i < headers.Count; i++)
						Assert.AreEqual ("Test inline image", headers[i][HeaderId.Subject]);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetMessageHeaders: {0}", ex);
				}

				try {
					using (var stream = await client.GetStreamAsync (0)) {
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStream: {0}", ex);
				}

				try {
					var streams = await client.GetStreamsAsync (0, 3);

					Assert.AreEqual (3, streams.Count);
					for (int i = 0; i < 3; i++) {
						streams[i].Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStreams: {0}", ex);
				}

				try {
					var streams = await client.GetStreamsAsync (new int[] { 0, 1, 2 });

					Assert.AreEqual (3, streams.Count);
					for (int i = 0; i < 3; i++) {
						streams[i].Dispose ();
					}
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in GetStreams: {0}", ex);
				}

				try {
					await client.NoOpAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in NoOp: {0}", ex);
				}

				try {
					await client.DeleteMessageAsync (0);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessage: {0}", ex);
				}

				try {
					await client.ResetAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					await client.DeleteMessagesAsync (new [] { 0, 1, 2 });
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessages: {0}", ex);
				}

				try {
					await client.ResetAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					await client.DeleteMessagesAsync (0, 3);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteMessages: {0}", ex);
				}

				try {
					await client.ResetAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					await client.DeleteAllMessagesAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in DeleteAllMessages: {0}", ex);
				}

				try {
					await client.ResetAsync ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Reset: {0}", ex);
				}

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public Task TestGMailPop3ClientAsync ()
		{
			return TestGMailPop3ClientAsync (CreateGMailCommands (), false);
		}

		List<Pop3ReplayCommand> CreateGMailCommandsNoPipelining ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL\r\n", "gmail.uidl.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 1\r\n", "gmail.uidl1.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 2\r\n", "gmail.uidl2.txt"));
			commands.Add (new Pop3ReplayCommand ("UIDL 3\r\n", "gmail.uidl3.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST\r\n", "gmail.list.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 1\r\n", "gmail.list1.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 2\r\n", "gmail.list2.txt"));
			commands.Add (new Pop3ReplayCommand ("LIST 3\r\n", "gmail.list3.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 2 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 3 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 2 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 3 0\r\n", "gmail.top.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("NOOP\r\n", "gmail.noop.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 2\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 3\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 2\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 3\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 1\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 2\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("DELE 3\r\n", "gmail.dele.txt"));
			commands.Add (new Pop3ReplayCommand ("RSET\r\n", "gmail.rset.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			return commands;
		}

		[Test]
		public void TestGMailPop3ClientNoPipelining ()
		{
			TestGMailPop3Client (CreateGMailCommandsNoPipelining (), true);
		}

		[Test]
		public Task TestGMailPop3ClientNoPipeliningAsync ()
		{
			return TestGMailPop3ClientAsync (CreateGMailCommandsNoPipelining (), true);
		}

		[Test]
		public void TestGetEnumerator ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 2\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 3\r\n", "gmail.retr1.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				int count = 0;
				foreach (var message in client)
					count++;
				Assert.AreEqual (3, count);

				count = 0;
				foreach (var message in (IEnumerable) client)
					count++;
				Assert.AreEqual (3, count);

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestLangExtension ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LANG\r\n", "lang.getlang.txt"));
			commands.Add (new Pop3ReplayCommand ("LANG en\r\n", "lang.setlang.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (LangCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.EnableUTF8 ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
				}

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (LangCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				var languages = client.GetLanguages ();
				Assert.AreEqual (6, languages.Count);
				Assert.AreEqual ("en", languages[0].Language);
				Assert.AreEqual ("English", languages[0].Description);
				Assert.AreEqual ("en-boont", languages[1].Language);
				Assert.AreEqual ("English Boontling dialect", languages[1].Description);
				Assert.AreEqual ("de", languages[2].Language);
				Assert.AreEqual ("Deutsch", languages[2].Description);
				Assert.AreEqual ("it", languages[3].Language);
				Assert.AreEqual ("Italiano", languages[3].Description);
				Assert.AreEqual ("es", languages[4].Language);
				Assert.AreEqual ("Espanol", languages[4].Description);
				Assert.AreEqual ("sv", languages[5].Language);
				Assert.AreEqual ("Svenska", languages[5].Description);

				client.SetLanguage ("en");

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestLangExtensionAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "lang.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("UTF8\r\n", "lang.utf8.txt"));
			commands.Add (new Pop3ReplayCommand ("APOP username d99894e8445daf54c4ce781ef21331b7\r\n", "lang.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "lang.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "lang.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("LANG\r\n", "lang.getlang.txt"));
			commands.Add (new Pop3ReplayCommand ("LANG en\r\n", "lang.setlang.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (LangCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					await client.EnableUTF8Async ();
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in EnableUTF8: {0}", ex);
				}

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (LangCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);

				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				var languages = await client.GetLanguagesAsync ();
				Assert.AreEqual (6, languages.Count);
				Assert.AreEqual ("en", languages[0].Language);
				Assert.AreEqual ("English", languages[0].Description);
				Assert.AreEqual ("en-boont", languages[1].Language);
				Assert.AreEqual ("English Boontling dialect", languages[1].Description);
				Assert.AreEqual ("de", languages[2].Language);
				Assert.AreEqual ("Deutsch", languages[2].Description);
				Assert.AreEqual ("it", languages[3].Language);
				Assert.AreEqual ("Italiano", languages[3].Description);
				Assert.AreEqual ("es", languages[4].Language);
				Assert.AreEqual ("Espanol", languages[4].Description);
				Assert.AreEqual ("sv", languages[5].Language);
				Assert.AreEqual ("Svenska", languages[5].Description);

				await client.SetLanguageAsync ("en");

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestLangNotSupported ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				Assert.Throws<NotSupportedException> (() => client.GetLanguages ());
				Assert.Throws<NotSupportedException> (() => client.SetLanguage ("en"));

				Assert.ThrowsAsync<NotSupportedException> (() => client.GetLanguagesAsync ());
				Assert.ThrowsAsync<NotSupportedException> (() => client.SetLanguageAsync ("en"));

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public void TestParseExceptions ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1-parse-error.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.retr1-parse-error.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					client.ReplayConnect ("localhost", new Pop3ReplayStream (commands, false, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					client.Authenticate ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				try {
					client.GetMessage (0);
					Assert.Fail ("Expected GetMessage() to throw Pop3ProtocolException");
				} catch (Pop3ProtocolException pex) {
					Assert.IsInstanceOf<FormatException> (pex.InnerException);
				} catch (Exception ex) {
					Assert.Fail ("Unexpected exception thrown by GetMessage: {0}", ex);
				}

				try {
					client.GetMessageHeaders (0);
					Assert.Fail ("Expected GetMessageHeaders() to throw Pop3ProtocolException");
				} catch (Pop3ProtocolException pex) {
					Assert.IsInstanceOf<FormatException> (pex.InnerException);
				} catch (Exception ex) {
					Assert.Fail ("Unexpected exception thrown by GetMessageHeaders: {0}", ex);
				}

				try {
					client.Disconnect (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}

		[Test]
		public async Task TestParseExceptionsAsync ()
		{
			var commands = new List<Pop3ReplayCommand> ();
			commands.Add (new Pop3ReplayCommand ("", "gmail.greeting.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa1.txt"));
			commands.Add (new Pop3ReplayCommand ("AUTH PLAIN\r\n", "gmail.plus.txt"));
			commands.Add (new Pop3ReplayCommand ("AHVzZXJuYW1lAHBhc3N3b3Jk\r\n", "gmail.auth.txt"));
			commands.Add (new Pop3ReplayCommand ("CAPA\r\n", "gmail.capa2.txt"));
			commands.Add (new Pop3ReplayCommand ("STAT\r\n", "gmail.stat.txt"));
			commands.Add (new Pop3ReplayCommand ("RETR 1\r\n", "gmail.retr1-parse-error.txt"));
			commands.Add (new Pop3ReplayCommand ("TOP 1 0\r\n", "gmail.retr1-parse-error.txt"));
			commands.Add (new Pop3ReplayCommand ("QUIT\r\n", "gmail.quit.txt"));

			using (var client = new Pop3Client ()) {
				try {
					await client.ReplayConnectAsync ("localhost", new Pop3ReplayStream (commands, true, false));
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Connect: {0}", ex);
				}

				Assert.IsTrue (client.IsConnected, "Client failed to connect.");

				Assert.AreEqual (GMailCapa1, client.Capabilities);
				Assert.AreEqual (2, client.AuthenticationMechanisms.Count);
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("XOAUTH2"), "Expected SASL XOAUTH2 auth mechanism");
				Assert.IsTrue (client.AuthenticationMechanisms.Contains ("PLAIN"), "Expected SASL PLAIN auth mechanism");

				try {
					await client.AuthenticateAsync ("username", "password");
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Authenticate: {0}", ex);
				}

				Assert.AreEqual (GMailCapa2, client.Capabilities);
				Assert.AreEqual (0, client.AuthenticationMechanisms.Count);
				Assert.AreEqual (3, client.Count, "Expected 3 messages");
				Assert.AreEqual (221409, client.Size, "Expected 221409 octets");

				try {
					await client.GetMessageAsync (0);
					Assert.Fail ("Expected GetMessageAsync() to throw Pop3ProtocolException");
				} catch (Pop3ProtocolException pex) {
					Assert.IsInstanceOf<FormatException> (pex.InnerException);
				} catch (Exception ex) {
					Assert.Fail ("Unexpected exception thrown by GetMessageAsync: {0}", ex);
				}

				try {
					await client.GetMessageHeadersAsync (0);
					Assert.Fail ("Expected GetMessageHeadersAsync() to throw Pop3ProtocolException");
				} catch (Pop3ProtocolException pex) {
					Assert.IsInstanceOf<FormatException> (pex.InnerException);
				} catch (Exception ex) {
					Assert.Fail ("Unexpected exception thrown by GetMessageHeadersAsync: {0}", ex);
				}

				try {
					await client.DisconnectAsync (true);
				} catch (Exception ex) {
					Assert.Fail ("Did not expect an exception in Disconnect: {0}", ex);
				}

				Assert.IsFalse (client.IsConnected, "Failed to disconnect");
			}
		}
	}
}
