﻿//
// ImapFolderFetch.cs
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
using System.Linq;
using System.Text;
using System.Buffers;
using System.Threading;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;

using MimeKit;
using MimeKit.IO;
using MimeKit.Text;
using MimeKit.Utils;

using MailKit.Search;

namespace MailKit.Net.Imap
{
	public partial class ImapFolder
	{
		const int PreviewHtmlLength = 16 * 1024;
		const int PreviewTextLength = 512;
		const int BufferSize = 4096;

		class FetchSummaryContext
		{
			public readonly List<IMessageSummary> Messages;

			public FetchSummaryContext (int capacity)
			{
				Messages = new List<IMessageSummary> (capacity);
			}

			int BinarySearch (int index, bool insert)
			{
				int min = 0, max = Messages.Count;

				if (max == 0)
					return insert ? 0 : -1;

				if (insert && index > Messages[max - 1].Index)
					return max;

				do {
					int i = min + ((max - min) / 2);

					if (index == Messages[i].Index)
						return i;

					if (index > Messages[i].Index) {
						min = i + 1;
					} else {
						max = i;
					}
				} while (min < max);

				return insert ? min : -1;
			}

			public void Add (int index, MessageSummary message)
			{
				int i = BinarySearch (index, true);

				if (i < Messages.Count)
					Messages.Insert (i, message);
				else
					Messages.Add (message);
			}

			public bool TryGetValue (int index, out MessageSummary message)
			{
				int i;

				if ((i = BinarySearch (index, false)) == -1) {
					message = null;
					return false;
				}

				message = (MessageSummary) Messages[i];

				return true;
			}

			public void OnMessageExpunged (object sender, MessageEventArgs args)
			{
				int index = BinarySearch (args.Index, false);

				if (index == -1)
					return;

				Messages.RemoveAt (index);

				for (int i = index; i < Messages.Count; i++) {
					var message = (MessageSummary) Messages[i];
					message.Index--;
				}
			}
		}

		static void ReadLiteralData (ImapEngine engine, CancellationToken cancellationToken)
		{
			var buf = ArrayPool<byte>.Shared.Rent (BufferSize);
			int nread;

			try {
				do {
					nread = engine.Stream.Read (buf, 0, BufferSize, cancellationToken);
				} while (nread > 0);
			} finally {
				ArrayPool<byte>.Shared.Return (buf);
			}
		}

		static async Task ReadLiteralDataAsync (ImapEngine engine, CancellationToken cancellationToken)
		{
			var buf = ArrayPool<byte>.Shared.Rent (BufferSize);
			int nread;

			try {
				do {
					nread = await engine.Stream.ReadAsync (buf, 0, BufferSize, cancellationToken).ConfigureAwait (false);
				} while (nread > 0);
			} finally {
				ArrayPool<byte>.Shared.Return (buf);
			}
		}

		static void SkipParenthesizedList (ImapEngine engine, CancellationToken cancellationToken)
		{
			do {
				var token = engine.PeekToken (cancellationToken);

				if (token.Type == ImapTokenType.Eoln)
					return;

				// token is safe to read, so pop it off the queue
				engine.ReadToken (cancellationToken);

				if (token.Type == ImapTokenType.CloseParen)
					break;

				if (token.Type == ImapTokenType.OpenParen) {
					// skip the inner parenthesized list
					SkipParenthesizedList (engine, cancellationToken);
				}
			} while (true);
		}

		static async Task SkipParenthesizedListAsync (ImapEngine engine, CancellationToken cancellationToken)
		{
			do {
				var token = await engine.PeekTokenAsync (cancellationToken).ConfigureAwait (false);

				if (token.Type == ImapTokenType.Eoln)
					return;

				// token is safe to read, so pop it off the queue
				await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

				if (token.Type == ImapTokenType.CloseParen)
					break;

				if (token.Type == ImapTokenType.OpenParen) {
					// skip the inner parenthesized list
					await SkipParenthesizedListAsync (engine, cancellationToken).ConfigureAwait (false);
				}
			} while (true);
		}

		static Task SkipParenthesizedListAsync (ImapEngine engine, bool doAsync, CancellationToken cancellationToken)
		{
			if (doAsync)
				return SkipParenthesizedListAsync (engine, cancellationToken);

			SkipParenthesizedList (engine, cancellationToken);

			return Task.CompletedTask;
		}

		static DateTimeOffset? ReadDateTimeOffsetToken (ImapEngine engine, string atom, CancellationToken cancellationToken)
		{
			var token = engine.ReadToken (cancellationToken);

			switch (token.Type) {
			case ImapTokenType.QString:
			case ImapTokenType.Atom:
				return ImapUtils.ParseInternalDate ((string) token.Value);
			case ImapTokenType.Nil:
				return null;
			default:
				throw ImapEngine.UnexpectedToken (ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
			}
		}

		static async Task<DateTimeOffset?> ReadDateTimeOffsetTokenAsync (ImapEngine engine, string atom, CancellationToken cancellationToken)
		{
			var token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

			switch (token.Type) {
			case ImapTokenType.QString:
			case ImapTokenType.Atom:
				return ImapUtils.ParseInternalDate ((string) token.Value);
			case ImapTokenType.Nil:
				return null;
			default:
				throw ImapEngine.UnexpectedToken (ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
			}
		}

		delegate void FetchSummaryItemsCompletedCallback (MessageSummary message);

		void FetchSummaryItems (ImapEngine engine, MessageSummary message, FetchSummaryItemsCompletedCallback completed, CancellationToken cancellationToken)
		{
			var token = engine.ReadToken (cancellationToken);

			ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			do {
				token = engine.ReadToken (cancellationToken);

				if (token.Type == ImapTokenType.CloseParen || token.Type == ImapTokenType.Eoln)
					break;

				bool parenthesized = false;
				if (engine.QuirksMode == ImapQuirksMode.Domino && token.Type == ImapTokenType.OpenParen) {
					// Note: Lotus Domino IMAP will (sometimes?) encapsulate the `ENVELOPE` segment of the
					// response within an extra set of parenthesis.
					//
					// See https://github.com/jstedfast/MailKit/issues/943 for details.
					token = engine.ReadToken (cancellationToken);
					parenthesized = true;
				}

				ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

				var atom = (string) token.Value;
				string format;
				ulong value64;
				uint value;
				int idx;

				if (atom.Equals ("INTERNALDATE", StringComparison.OrdinalIgnoreCase)) {
					message.InternalDate = ReadDateTimeOffsetToken (engine, atom, cancellationToken);
					message.Fields |= MessageSummaryItems.InternalDate;
				} else if (atom.Equals ("SAVEDATE", StringComparison.OrdinalIgnoreCase)) {
					message.SaveDate = ReadDateTimeOffsetToken (engine, atom, cancellationToken);
					message.Fields |= MessageSummaryItems.SaveDate;
				} else if (atom.Equals ("RFC822.SIZE", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					message.Size = ImapEngine.ParseNumber (token, false, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.Size;
				} else if (atom.Equals ("BODYSTRUCTURE", StringComparison.OrdinalIgnoreCase)) {
					format = string.Format (ImapEngine.GenericItemSyntaxErrorFormat, "BODYSTRUCTURE", "{0}");
					message.Body = ImapUtils.ParseBodyAsync (engine, format, string.Empty, false, cancellationToken).GetAwaiter ().GetResult ();
					message.Fields |= MessageSummaryItems.BodyStructure;
				} else if (atom.Equals ("BODY", StringComparison.OrdinalIgnoreCase)) {
					token = engine.PeekToken (cancellationToken);
					format = ImapEngine.FetchBodySyntaxErrorFormat;

					if (token.Type == ImapTokenType.OpenBracket) {
						var referencesField = false;
						var headerFields = false;

						// consume the '['
						token = engine.ReadToken (cancellationToken);

						ImapEngine.AssertToken (token, ImapTokenType.OpenBracket, format, token);

						// References and/or other headers were requested...

						do {
							token = engine.ReadToken (cancellationToken);

							if (token.Type == ImapTokenType.CloseBracket)
								break;

							if (token.Type == ImapTokenType.OpenParen) {
								do {
									token = engine.ReadToken (cancellationToken);

									if (token.Type == ImapTokenType.CloseParen)
										break;

									// the header field names will generally be atoms or qstrings but may also be literals
									engine.Stream.UngetToken (token);

									var field = ImapUtils.ReadStringToken (engine, format, cancellationToken);

									if (headerFields && !referencesField && field.Equals ("REFERENCES", StringComparison.OrdinalIgnoreCase))
										referencesField = true;
								} while (true);
							} else {
								ImapEngine.AssertToken (token, ImapTokenType.Atom, format, token);

								atom = (string) token.Value;

								headerFields = atom.Equals ("HEADER.FIELDS", StringComparison.OrdinalIgnoreCase);

								if (!headerFields && atom.Equals ("HEADER", StringComparison.OrdinalIgnoreCase)) {
									// if we're fetching *all* headers, then it will include the References header (if it exists)
									referencesField = true;
								}
							}
						} while (true);

						ImapEngine.AssertToken (token, ImapTokenType.CloseBracket, format, token);

						token = engine.ReadToken (cancellationToken);

						ImapEngine.AssertToken (token, ImapTokenType.Literal, format, token);

						try {
							message.Headers = engine.ParseHeadersAsync (engine.Stream, false, cancellationToken).GetAwaiter ().GetResult ();
						} catch (FormatException) {
							message.Headers = new HeaderList ();
						}

						// consume any remaining literal data... (typically extra blank lines)
						ReadLiteralData (engine, cancellationToken);

						message.References = new MessageIdList ();

						if ((idx = message.Headers.IndexOf (HeaderId.References)) != -1) {
							var references = message.Headers[idx];
							var rawValue = references.RawValue;

							foreach (var msgid in MimeUtils.EnumerateReferences (rawValue, 0, rawValue.Length))
								message.References.Add (msgid);
						}

						message.Fields |= MessageSummaryItems.Headers;

						if (referencesField)
							message.Fields |= MessageSummaryItems.References;
					} else {
						message.Body = ImapUtils.ParseBodyAsync (engine, format, string.Empty, false, cancellationToken).GetAwaiter ().GetResult ();
						message.Fields |= MessageSummaryItems.Body;
					}
				} else if (atom.Equals ("ENVELOPE", StringComparison.OrdinalIgnoreCase)) {
					message.Envelope = ImapUtils.ParseEnvelopeAsync (engine, false, cancellationToken).GetAwaiter ().GetResult ();
					message.Fields |= MessageSummaryItems.Envelope;
				} else if (atom.Equals ("FLAGS", StringComparison.OrdinalIgnoreCase)) {
					message.Flags = ImapUtils.ParseFlagsListAsync (engine, atom, (HashSet<string>) message.Keywords, false, cancellationToken).GetAwaiter ().GetResult ();
					message.Fields |= MessageSummaryItems.Flags;
				} else if (atom.Equals ("MODSEQ", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = engine.ReadToken (cancellationToken);

					value64 = ImapEngine.ParseNumber64 (token, false, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = engine.ReadToken (cancellationToken);

					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.Fields |= MessageSummaryItems.ModSeq;
					message.ModSeq = value64;

					if (value64 > HighestModSeq)
						UpdateHighestModSeq (value64);
				} else if (atom.Equals ("UID", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					value = ImapEngine.ParseNumber (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.UniqueId = new UniqueId (UidValidity, value);
					message.Fields |= MessageSummaryItems.UniqueId;
				} else if (atom.Equals ("EMAILID", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = engine.ReadToken (cancellationToken);

					ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.Fields |= MessageSummaryItems.EmailId;
					message.EmailId = (string) token.Value;

					token = engine.ReadToken (cancellationToken);

					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
				} else if (atom.Equals ("THREADID", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					if (token.Type == ImapTokenType.OpenParen) {
						token = engine.ReadToken (cancellationToken);

						ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

						message.Fields |= MessageSummaryItems.ThreadId;
						message.ThreadId = (string) token.Value;

						token = engine.ReadToken (cancellationToken);

						ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					} else {
						ImapEngine.AssertToken (token, ImapTokenType.Nil, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

						message.Fields |= MessageSummaryItems.ThreadId;
						message.ThreadId = null;
					}
				} else if (atom.Equals ("X-GM-MSGID", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					value64 = ImapEngine.ParseNumber64 (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.GMailMessageId;
					message.GMailMessageId = value64;
				} else if (atom.Equals ("X-GM-THRID", StringComparison.OrdinalIgnoreCase)) {
					token = engine.ReadToken (cancellationToken);

					value64 = ImapEngine.ParseNumber64 (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.GMailThreadId;
					message.GMailThreadId = value64;
				} else if (atom.Equals ("X-GM-LABELS", StringComparison.OrdinalIgnoreCase)) {
					message.GMailLabels = ImapUtils.ParseLabelsListAsync (engine, false, cancellationToken).GetAwaiter ().GetResult ();
					message.Fields |= MessageSummaryItems.GMailLabels;
				} else if (atom.Equals ("ANNOTATION", StringComparison.OrdinalIgnoreCase)) {
					message.Annotations = ImapUtils.ParseAnnotationsAsync (engine, false, cancellationToken).GetAwaiter ().GetResult ();
					message.Fields |= MessageSummaryItems.Annotations;
				} else if (atom.Equals ("PREVIEW", StringComparison.OrdinalIgnoreCase)) {
					format = string.Format (ImapEngine.GenericItemSyntaxErrorFormat, "PREVIEW", "{0}");
					message.PreviewText = ImapUtils.ReadNStringToken (engine, format, false, cancellationToken);
					message.Fields |= MessageSummaryItems.PreviewText;
				} else {
					// Unexpected or unknown token (such as XAOL.SPAM.REASON or XAOL-MSGID). Simply read 1 more token (the argument) and ignore.
					token = engine.ReadToken (cancellationToken);

					if (token.Type == ImapTokenType.OpenParen)
						SkipParenthesizedList (engine, cancellationToken);
				}

				if (parenthesized) {
					// Note: This is the second half of the Lotus Domino IMAP server work-around.
					token = engine.ReadToken (cancellationToken);
					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);
				}
			} while (true);

			ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			completed (message);
		}

		async Task FetchSummaryItemsAsync (ImapEngine engine, MessageSummary message, FetchSummaryItemsCompletedCallback completed, CancellationToken cancellationToken)
		{
			var token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

			ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			do {
				token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

				if (token.Type == ImapTokenType.CloseParen || token.Type == ImapTokenType.Eoln)
					break;

				bool parenthesized = false;
				if (engine.QuirksMode == ImapQuirksMode.Domino && token.Type == ImapTokenType.OpenParen) {
					// Note: Lotus Domino IMAP will (sometimes?) encapsulate the `ENVELOPE` segment of the
					// response within an extra set of parenthesis.
					//
					// See https://github.com/jstedfast/MailKit/issues/943 for details.
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);
					parenthesized = true;
				}

				ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

				var atom = (string) token.Value;
				string format;
				ulong value64;
				uint value;
				int idx;

				if (atom.Equals ("INTERNALDATE", StringComparison.OrdinalIgnoreCase)) {
					message.InternalDate = await ReadDateTimeOffsetTokenAsync (engine, atom, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.InternalDate;
				} else if (atom.Equals ("SAVEDATE", StringComparison.OrdinalIgnoreCase)) {
					message.SaveDate = await ReadDateTimeOffsetTokenAsync (engine, atom, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.SaveDate;
				} else if (atom.Equals ("RFC822.SIZE", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					message.Size = ImapEngine.ParseNumber (token, false, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.Size;
				} else if (atom.Equals ("BODYSTRUCTURE", StringComparison.OrdinalIgnoreCase)) {
					format = string.Format (ImapEngine.GenericItemSyntaxErrorFormat, "BODYSTRUCTURE", "{0}");
					message.Body = await ImapUtils.ParseBodyAsync (engine, format, string.Empty, true, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.BodyStructure;
				} else if (atom.Equals ("BODY", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.PeekTokenAsync (cancellationToken).ConfigureAwait (false);
					format = ImapEngine.FetchBodySyntaxErrorFormat;

					if (token.Type == ImapTokenType.OpenBracket) {
						var referencesField = false;
						var headerFields = false;

						// consume the '['
						token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

						ImapEngine.AssertToken (token, ImapTokenType.OpenBracket, format, token);

						// References and/or other headers were requested...

						do {
							token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

							if (token.Type == ImapTokenType.CloseBracket)
								break;

							if (token.Type == ImapTokenType.OpenParen) {
								do {
									token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

									if (token.Type == ImapTokenType.CloseParen)
										break;

									// the header field names will generally be atoms or qstrings but may also be literals
									engine.Stream.UngetToken (token);

									var field = await ImapUtils.ReadStringTokenAsync (engine, format, cancellationToken).ConfigureAwait (false);

									if (headerFields && !referencesField && field.Equals ("REFERENCES", StringComparison.OrdinalIgnoreCase))
										referencesField = true;
								} while (true);
							} else {
								ImapEngine.AssertToken (token, ImapTokenType.Atom, format, token);

								atom = (string) token.Value;

								headerFields = atom.Equals ("HEADER.FIELDS", StringComparison.OrdinalIgnoreCase);

								if (!headerFields && atom.Equals ("HEADER", StringComparison.OrdinalIgnoreCase)) {
									// if we're fetching *all* headers, then it will include the References header (if it exists)
									referencesField = true;
								}
							}
						} while (true);

						ImapEngine.AssertToken (token, ImapTokenType.CloseBracket, format, token);

						token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

						ImapEngine.AssertToken (token, ImapTokenType.Literal, format, token);

						try {
							message.Headers = await engine.ParseHeadersAsync (engine.Stream, true, cancellationToken).ConfigureAwait (false);
						} catch (FormatException) {
							message.Headers = new HeaderList ();
						}

						// consume any remaining literal data... (typically extra blank lines)
						await ReadLiteralDataAsync (engine, cancellationToken).ConfigureAwait (false);

						message.References = new MessageIdList ();

						if ((idx = message.Headers.IndexOf (HeaderId.References)) != -1) {
							var references = message.Headers[idx];
							var rawValue = references.RawValue;

							foreach (var msgid in MimeUtils.EnumerateReferences (rawValue, 0, rawValue.Length))
								message.References.Add (msgid);
						}

						message.Fields |= MessageSummaryItems.Headers;

						if (referencesField)
							message.Fields |= MessageSummaryItems.References;
					} else {
						message.Body = await ImapUtils.ParseBodyAsync (engine, format, string.Empty, true, cancellationToken).ConfigureAwait (false);
						message.Fields |= MessageSummaryItems.Body;
					}
				} else if (atom.Equals ("ENVELOPE", StringComparison.OrdinalIgnoreCase)) {
					message.Envelope = await ImapUtils.ParseEnvelopeAsync (engine, true, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.Envelope;
				} else if (atom.Equals ("FLAGS", StringComparison.OrdinalIgnoreCase)) {
					message.Flags = await ImapUtils.ParseFlagsListAsync (engine, atom, (HashSet<string>) message.Keywords, true, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.Flags;
				} else if (atom.Equals ("MODSEQ", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					value64 = ImapEngine.ParseNumber64 (token, false, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.Fields |= MessageSummaryItems.ModSeq;
					message.ModSeq = value64;

					if (value64 > HighestModSeq)
						UpdateHighestModSeq (value64);
				} else if (atom.Equals ("UID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					value = ImapEngine.ParseNumber (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.UniqueId = new UniqueId (UidValidity, value);
					message.Fields |= MessageSummaryItems.UniqueId;
				} else if (atom.Equals ("EMAILID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					message.Fields |= MessageSummaryItems.EmailId;
					message.EmailId = (string) token.Value;

					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
				} else if (atom.Equals ("THREADID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					if (token.Type == ImapTokenType.OpenParen) {
						token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

						ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

						message.Fields |= MessageSummaryItems.ThreadId;
						message.ThreadId = (string) token.Value;

						token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

						ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					} else {
						ImapEngine.AssertToken (token, ImapTokenType.Nil, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

						message.Fields |= MessageSummaryItems.ThreadId;
						message.ThreadId = null;
					}
				} else if (atom.Equals ("X-GM-MSGID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					value64 = ImapEngine.ParseNumber64 (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.GMailMessageId;
					message.GMailMessageId = value64;
				} else if (atom.Equals ("X-GM-THRID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					value64 = ImapEngine.ParseNumber64 (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					message.Fields |= MessageSummaryItems.GMailThreadId;
					message.GMailThreadId = value64;
				} else if (atom.Equals ("X-GM-LABELS", StringComparison.OrdinalIgnoreCase)) {
					message.GMailLabels = await ImapUtils.ParseLabelsListAsync (engine, true, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.GMailLabels;
				} else if (atom.Equals ("ANNOTATION", StringComparison.OrdinalIgnoreCase)) {
					message.Annotations = await ImapUtils.ParseAnnotationsAsync (engine, true, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.Annotations;
				} else if (atom.Equals ("PREVIEW", StringComparison.OrdinalIgnoreCase)) {
					format = string.Format (ImapEngine.GenericItemSyntaxErrorFormat, "PREVIEW", "{0}");
					message.PreviewText = await ImapUtils.ReadNStringTokenAsync (engine, format, false, cancellationToken).ConfigureAwait (false);
					message.Fields |= MessageSummaryItems.PreviewText;
				} else {
					// Unexpected or unknown token (such as XAOL.SPAM.REASON or XAOL-MSGID). Simply read 1 more token (the argument) and ignore.
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);

					if (token.Type == ImapTokenType.OpenParen)
						await SkipParenthesizedListAsync (engine, cancellationToken).ConfigureAwait (false);
				}

				if (parenthesized) {
					// Note: This is the second half of the Lotus Domino IMAP server work-around.
					token = await engine.ReadTokenAsync (cancellationToken).ConfigureAwait (false);
					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);
				}
			} while (true);

			ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			completed (message);
		}

		Task FetchSummaryItemsAsync (ImapEngine engine, ImapCommand ic, int index, bool doAsync)
		{
			var ctx = (FetchSummaryContext) ic.UserData;

			if (!ctx.TryGetValue (index, out var message)) {
				message = new MessageSummary (this, index);
				ctx.Add (index, message);
			}

			if (doAsync)
				return FetchSummaryItemsAsync (engine, message, OnMessageSummaryFetched, ic.CancellationToken);

			FetchSummaryItems (engine, message, OnMessageSummaryFetched, ic.CancellationToken);

			return Task.CompletedTask;
		}

		internal static string FormatSummaryItems (ImapEngine engine, IFetchRequest request, out bool previewText, bool isNotify = false)
		{
			var items = request.Items;

			if ((engine.Capabilities & ImapCapabilities.Preview) == 0 && (items & MessageSummaryItems.PreviewText) != 0) {
				// if the user wants the preview text, we will also need the UIDs and BODYSTRUCTUREs
				// so that we can request a preview of the body text in subsequent FETCH requests.
				items |= MessageSummaryItems.BodyStructure | MessageSummaryItems.UniqueId;
				items &= ~MessageSummaryItems.PreviewText;
				previewText = true;
			} else {
				previewText = false;
			}

			if ((items & MessageSummaryItems.BodyStructure) != 0 && (items & MessageSummaryItems.Body) != 0) {
				// don't query both the BODY and BODYSTRUCTURE, that's just dumb...
				items &= ~MessageSummaryItems.Body;
			}

			if (engine.QuirksMode != ImapQuirksMode.GMail && !isNotify) {
				if (items == MessageSummaryItems.All)
					return "ALL";

				if (items == MessageSummaryItems.Full)
					return "FULL";

				if (items == MessageSummaryItems.Fast)
					return "FAST";
			}

			var tokens = new List<string> ();

			// now add on any additional summary items...
			if ((items & MessageSummaryItems.UniqueId) != 0)
				tokens.Add ("UID");
			if ((items & MessageSummaryItems.Flags) != 0)
				tokens.Add ("FLAGS");
			if ((items & MessageSummaryItems.InternalDate) != 0)
				tokens.Add ("INTERNALDATE");
			if ((items & MessageSummaryItems.Size) != 0)
				tokens.Add ("RFC822.SIZE");
			if ((items & MessageSummaryItems.Envelope) != 0)
				tokens.Add ("ENVELOPE");
			if ((items & MessageSummaryItems.BodyStructure) != 0)
				tokens.Add ("BODYSTRUCTURE");
			if ((items & MessageSummaryItems.Body) != 0)
				tokens.Add ("BODY");

			if ((engine.Capabilities & ImapCapabilities.CondStore) != 0) {
				if ((items & MessageSummaryItems.ModSeq) != 0)
					tokens.Add ("MODSEQ");
			}

			if ((engine.Capabilities & ImapCapabilities.Annotate) != 0) {
				if ((items & MessageSummaryItems.Annotations) != 0)
					tokens.Add ("ANNOTATION (/* (value size))");
			}

			if ((engine.Capabilities & ImapCapabilities.ObjectID) != 0) {
				if ((items & MessageSummaryItems.EmailId) != 0)
					tokens.Add ("EMAILID");
				if ((items & MessageSummaryItems.ThreadId) != 0)
					tokens.Add ("THREADID");
			}

			if ((engine.Capabilities & ImapCapabilities.SaveDate) != 0) {
				if ((items & MessageSummaryItems.SaveDate) != 0)
					tokens.Add ("SAVEDATE");
			}

			if ((engine.Capabilities & ImapCapabilities.Preview) != 0) {
				if ((items & MessageSummaryItems.PreviewText) != 0) {
#if ENABLE_LAZY_PREVIEW_API
					if (request.PreviewOptions == PreviewOptions.Lazy)
						tokens.Add ("PREVIEW (LAZY)");
					else
						tokens.Add ("PREVIEW");
#else
					tokens.Add ("PREVIEW");
#endif
				}
			}

			if ((engine.Capabilities & ImapCapabilities.GMailExt1) != 0) {
				// now for the GMail extension items
				if ((items & MessageSummaryItems.GMailMessageId) != 0)
					tokens.Add ("X-GM-MSGID");
				if ((items & MessageSummaryItems.GMailThreadId) != 0)
					tokens.Add ("X-GM-THRID");
				if ((items & MessageSummaryItems.GMailLabels) != 0)
					tokens.Add ("X-GM-LABELS");
			}

			if (request.Headers != null) {
				if (request.Headers.Count == 0 && request.Headers.Exclude) {
					tokens.Add ("BODY.PEEK[HEADER]");
				} else if (request.Headers.Exclude) {
					var headerFields = new StringBuilder ("BODY.PEEK[HEADER.FIELDS.NOT (");

					foreach (var header in request.Headers) {
						headerFields.Append (header);
						headerFields.Append (' ');
					}

					headerFields[headerFields.Length - 1] = ')';
					headerFields.Append (']');

					tokens.Add (headerFields.ToString ());
				} else {
					var headerFields = new StringBuilder ("BODY.PEEK[HEADER.FIELDS (");
					var references = false;

					foreach (var header in request.Headers) {
						if (header.Equals ("REFERENCES", StringComparison.Ordinal))
							references = true;

						headerFields.Append (header);
						headerFields.Append (' ');
					}

					if ((items & MessageSummaryItems.References) != 0 && !references)
						headerFields.Append ("REFERENCES ");

					headerFields[headerFields.Length - 1] = ')';
					headerFields.Append (']');

					tokens.Add (headerFields.ToString ());
				}
			} else if ((items & MessageSummaryItems.Headers) != 0) {
				tokens.Add ("BODY.PEEK[HEADER]");
			} else if ((items & MessageSummaryItems.References) != 0) {
				tokens.Add ("BODY.PEEK[HEADER.FIELDS (REFERENCES)]");
			}

			if (tokens.Count == 1 && !isNotify)
				return tokens[0];

			return string.Format ("({0})", string.Join (" ", tokens));
		}

		class FetchPreviewTextContext : FetchStreamContextBase
		{
			static readonly PlainTextPreviewer textPreviewer = new PlainTextPreviewer ();
			static readonly HtmlTextPreviewer htmlPreviewer = new HtmlTextPreviewer ();

			readonly FetchSummaryContext ctx;
			readonly ImapFolder folder;

			public FetchPreviewTextContext (ImapFolder folder, FetchSummaryContext ctx) : base (null)
			{
				this.folder = folder;
				this.ctx = ctx;
			}

			public override Task AddAsync (Section section, bool doAsync, CancellationToken cancellationToken)
			{
				if (!ctx.TryGetValue (section.Index, out var message))
					return Task.CompletedTask;

				var body = message.TextBody;
				TextPreviewer previewer;

				if (body == null) {
					previewer = htmlPreviewer;
					body = message.HtmlBody;
				} else {
					previewer = textPreviewer;
				}

				if (body == null)
					return Task.CompletedTask;

				var charset = body.ContentType.Charset ?? "utf-8";
				ContentEncoding encoding;

				if (string.IsNullOrEmpty (body.ContentTransferEncoding) || !MimeUtils.TryParse (body.ContentTransferEncoding, out encoding))
					encoding = ContentEncoding.Default;

				using (var memory = new MemoryStream ()) {
					var content = new MimeContent (section.Stream, encoding);

					content.DecodeTo (memory, cancellationToken);
					memory.Position = 0;

					try {
						message.PreviewText = previewer.GetPreviewText (memory, charset);
					} catch (DecoderFallbackException) {
						memory.Position = 0;

						message.PreviewText = previewer.GetPreviewText (memory, TextEncodings.Latin1);
					}

					message.Fields |= MessageSummaryItems.PreviewText;
					folder.OnMessageSummaryFetched (message);
				}

				return Task.CompletedTask;
			}

			public override Task SetUniqueIdAsync (int index, UniqueId uid, bool doAsync, CancellationToken cancellationToken)
			{
				return Task.CompletedTask;
			}
		}

		ImapCommand QueueFetchPreviewTextCommand (FetchSummaryContext sctx, KeyValuePair<string, UniqueIdSet> pair, int octets, CancellationToken cancellationToken)
		{
			var uids = pair.Value;
			string specifier;

			if (!string.IsNullOrEmpty (pair.Key))
				specifier = pair.Key;
			else
				specifier = "TEXT";

			// TODO: if the IMAP server supports the CONVERT extension, we could possibly use the
			// CONVERT command instead to decode *and* convert (html) into utf-8 plain text.
			//
			// e.g. "UID CONVERT {0} (\"text/plain\" (\"charset\" \"utf-8\")) BINARY[{1}]<0.{2}>\r\n"
			//
			// This would allow us to more accurately fetch X number of characters because we wouldn't
			// need to guestimate accounting for base64/quoted-printable decoding.

			var command = string.Format (CultureInfo.InvariantCulture, "UID FETCH {0} (BODY.PEEK[{1}]<0.{2}>)\r\n", uids, specifier, octets);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchPreviewTextContext (this, sctx);

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			return ic;
		}

		void FetchPreviewText (FetchSummaryContext sctx, Dictionary<string, UniqueIdSet> bodies, int octets, CancellationToken cancellationToken)
		{
			foreach (var pair in bodies) {
				var ic = QueueFetchPreviewTextCommand (sctx, pair, octets, cancellationToken);
				var ctx = (FetchPreviewTextContext) ic.UserData;

				try {
					Engine.Run (ic);

					ProcessResponseCodes (ic, null);

					if (ic.Response != ImapCommandResponse.Ok)
						throw ImapCommandException.Create ("FETCH", ic);
				} finally {
					ctx.Dispose ();
				}
			}
		}

		async Task FetchPreviewTextAsync (FetchSummaryContext sctx, Dictionary<string, UniqueIdSet> bodies, int octets, CancellationToken cancellationToken)
		{
			foreach (var pair in bodies) {
				var ic = QueueFetchPreviewTextCommand (sctx, pair, octets, cancellationToken);
				var ctx = (FetchPreviewTextContext) ic.UserData;

				try {
					await Engine.RunAsync (ic).ConfigureAwait (false);

					ProcessResponseCodes (ic, null);

					if (ic.Response != ImapCommandResponse.Ok)
						throw ImapCommandException.Create ("FETCH", ic);
				} finally {
					ctx.Dispose ();
				}
			}
		}

		void CreateFetchPreviewTextMappings (FetchSummaryContext sctx, out Dictionary<string, UniqueIdSet> textBodies, out Dictionary<string, UniqueIdSet> htmlBodies)
		{
			textBodies = new Dictionary<string, UniqueIdSet> ();
			htmlBodies = new Dictionary<string, UniqueIdSet> ();

			foreach (var item in sctx.Messages) {
				Dictionary<string, UniqueIdSet> bodies;
				var message = (MessageSummary) item;
				var body = message.TextBody;

				if (body == null) {
					body = message.HtmlBody;
					bodies = htmlBodies;
				} else {
					bodies = textBodies;
				}

				if (body == null || body.Octets == 0) {
					message.Fields |= MessageSummaryItems.PreviewText;
					message.PreviewText = string.Empty;
					OnMessageSummaryFetched (message);
					continue;
				}

				if (!bodies.TryGetValue (body.PartSpecifier, out var uids)) {
					uids = new UniqueIdSet (SortOrder.Ascending);
					bodies.Add (body.PartSpecifier, uids);
				}

				uids.Add (message.UniqueId);
			}
		}

		void GetPreviewText (FetchSummaryContext sctx, CancellationToken cancellationToken)
		{
			CreateFetchPreviewTextMappings (sctx, out var textBodies, out var htmlBodies);

			MessageExpunged += sctx.OnMessageExpunged;

			try {
				FetchPreviewText (sctx, textBodies, PreviewTextLength, cancellationToken);
				FetchPreviewText (sctx, htmlBodies, PreviewHtmlLength, cancellationToken);
			} finally {
				MessageExpunged -= sctx.OnMessageExpunged;
			}
		}

		async Task GetPreviewTextAsync (FetchSummaryContext sctx, CancellationToken cancellationToken)
		{
			CreateFetchPreviewTextMappings (sctx, out var textBodies, out var htmlBodies);

			MessageExpunged += sctx.OnMessageExpunged;

			try {
				await FetchPreviewTextAsync (sctx, textBodies, PreviewTextLength, cancellationToken).ConfigureAwait (false);
				await FetchPreviewTextAsync (sctx, htmlBodies, PreviewHtmlLength, cancellationToken).ConfigureAwait (false);
			} finally {
				MessageExpunged -= sctx.OnMessageExpunged;
			}
		}

		internal static bool IsEmptyFetchRequest (IFetchRequest request)
		{
			return request.Items == MessageSummaryItems.None && (request.Headers == null || (request.Headers.Count == 0 && !request.Headers.Exclude));
		}

		bool CheckCanFetch (IList<UniqueId> uids, IFetchRequest request)
		{
			if (uids == null)
				throw new ArgumentNullException (nameof (uids));

			if (request == null)
				throw new ArgumentNullException (nameof (request));

			if (request.ChangedSince.HasValue && !supportsModSeq)
				throw new NotSupportedException ("The ImapFolder does not support mod-sequences.");

			CheckState (true, false);

			return uids.Count > 0 && !IsEmptyFetchRequest (request);
		}

		string CreateFetchCommand (IList<UniqueId> uids, IFetchRequest request, out bool previewText)
		{
			var query = FormatSummaryItems (Engine, request, out previewText);
			var changedSince = string.Empty;

			if (request.ChangedSince.HasValue) {
				var vanished = Engine.QResyncEnabled ? " VANISHED" : string.Empty;

				changedSince = string.Format (CultureInfo.InvariantCulture, " (CHANGEDSINCE {0}{1})", request.ChangedSince.Value, vanished);
			}

			return string.Format ("UID FETCH %s {0}{1}\r\n", query, changedSince);
		}

		static int EstimateInitialCapacity (IList<UniqueId> uids)
		{
			if (uids is UniqueIdRange || uids is UniqueIdSet) {
				// UniqueIdRange is likely to refer to UIDs that have not yet been assigned or have been expunged,
				// so cap our maximum initial capacity to 1024 (a reasonable limit?).
				return Math.Min (uids.Count, 1024);
			}

			// If the user supplied an exact set of UIDs, then we'll assume they all exist
			// and therefore we can use the capacity of `uids` as our initial capacity.
			return uids.Count;
		}

		/// <summary>
		/// Fetches the message summaries for the specified message UIDs.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the specified message UIDs.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueId"/>
		/// </example>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="uids">The UIDs.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="uids"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="request"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="uids"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override IList<IMessageSummary> Fetch (IList<UniqueId> uids, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (uids, request))
				return Array.Empty<IMessageSummary> ();

			var command = CreateFetchCommand (uids, request, out bool previewText);
			var ctx = new FetchSummaryContext (EstimateInitialCapacity (uids));

			MessageExpunged += ctx.OnMessageExpunged;

			try {
				foreach (var ic in Engine.CreateCommands (cancellationToken, this, command, uids)) {
					ic.RegisterUntaggedHandler ("FETCH", FetchSummaryItemsAsync);
					ic.UserData = ctx;

					Engine.QueueCommand (ic);

					Engine.Run (ic);

					ProcessResponseCodes (ic, null);

					if (ic.Response != ImapCommandResponse.Ok)
						throw ImapCommandException.Create ("FETCH", ic);
				}
			} finally {
				MessageExpunged -= ctx.OnMessageExpunged;
			}

			if (previewText)
				GetPreviewText (ctx, cancellationToken);

			return ctx.Messages.AsReadOnly ();
		}

		/// <summary>
		/// Asynchronously fetches the message summaries for the specified message UIDs.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the specified message UIDs.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueId"/>
		/// </example>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="uids">The UIDs.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="uids"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="request"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="uids"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override async Task<IList<IMessageSummary>> FetchAsync (IList<UniqueId> uids, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (uids, request))
				return Array.Empty<IMessageSummary> ();

			var command = CreateFetchCommand (uids, request, out bool previewText);
			var ctx = new FetchSummaryContext (EstimateInitialCapacity (uids));

			MessageExpunged += ctx.OnMessageExpunged;

			try {
				foreach (var ic in Engine.CreateCommands (cancellationToken, this, command, uids)) {
					ic.RegisterUntaggedHandler ("FETCH", FetchSummaryItemsAsync);
					ic.UserData = ctx;

					Engine.QueueCommand (ic);

					await Engine.RunAsync (ic).ConfigureAwait (false);

					ProcessResponseCodes (ic, null);

					if (ic.Response != ImapCommandResponse.Ok)
						throw ImapCommandException.Create ("FETCH", ic);
				}
			} finally {
				MessageExpunged -= ctx.OnMessageExpunged;
			}

			if (previewText)
				await GetPreviewTextAsync (ctx, cancellationToken).ConfigureAwait (false);

			return ctx.Messages.AsReadOnly ();
		}

		bool CheckCanFetch (IList<int> indexes, IFetchRequest request)
		{
			if (indexes == null)
				throw new ArgumentNullException (nameof (indexes));

			if (request == null)
				throw new ArgumentNullException (nameof (request));

			if (request.ChangedSince.HasValue && !supportsModSeq)
				throw new NotSupportedException ("The ImapFolder does not support mod-sequences.");

			CheckState (true, false);
			CheckAllowIndexes ();

			return indexes.Count > 0 && !IsEmptyFetchRequest (request);
		}

		ImapCommand QueueFetchCommand (IList<int> indexes, IFetchRequest request, CancellationToken cancellationToken, out bool previewText)
		{
			var query = FormatSummaryItems (Engine, request, out previewText);
			var set = ImapUtils.FormatIndexSet (Engine, indexes);
			var changedSince = string.Empty;

			if (request.ChangedSince.HasValue)
				changedSince = string.Format (CultureInfo.InvariantCulture, " (CHANGEDSINCE {0})", request.ChangedSince.Value);

			var command = string.Format ("FETCH {0} {1}{2}\r\n", set, query, changedSince);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchSummaryContext (indexes.Count);

			ic.RegisterUntaggedHandler ("FETCH", FetchSummaryItemsAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			return ic;
		}

		/// <summary>
		/// Fetches the message summaries for the specified message indexes.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the specified message indexes.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="indexes">The indexes.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="indexes"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="request"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="indexes"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override IList<IMessageSummary> Fetch (IList<int> indexes, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (indexes, request))
				return Array.Empty<IMessageSummary> ();

			var ic = QueueFetchCommand (indexes, request, cancellationToken, out bool previewText);
			var ctx = (FetchSummaryContext) ic.UserData;

			Engine.Run (ic);

			ProcessResponseCodes (ic, null);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("FETCH", ic);

			if (previewText)
				GetPreviewText (ctx, cancellationToken);

			return ctx.Messages.AsReadOnly ();
		}

		/// <summary>
		/// Asynchronously fetches the message summaries for the specified message indexes.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the specified message indexes.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="indexes">The indexes.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="indexes"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="request"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="indexes"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override async Task<IList<IMessageSummary>> FetchAsync (IList<int> indexes, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (indexes, request))
				return Array.Empty<IMessageSummary> ();

			var ic = QueueFetchCommand (indexes, request, cancellationToken, out bool previewText);
			var ctx = (FetchSummaryContext) ic.UserData;

			await Engine.RunAsync (ic).ConfigureAwait (false);

			ProcessResponseCodes (ic, null);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("FETCH", ic);

			if (previewText)
				await GetPreviewTextAsync (ctx, cancellationToken).ConfigureAwait (false);

			return ctx.Messages.AsReadOnly ();
		}

		static string GetFetchRange (int min, int max)
		{
			var minValue = (min + 1).ToString (CultureInfo.InvariantCulture);

			if (min == max)
				return minValue;

			var maxValue = max != -1 ? (max + 1).ToString (CultureInfo.InvariantCulture) : "*";

			return string.Format (CultureInfo.InvariantCulture, "{0}:{1}", minValue, maxValue);
		}

		bool CheckCanFetch (int min, int max, IFetchRequest request)
		{
			if (min < 0)
				throw new ArgumentOutOfRangeException (nameof (min));

			if (max != -1 && max < min)
				throw new ArgumentOutOfRangeException (nameof (max));

			if (request == null)
				throw new ArgumentNullException (nameof (request));

			if (request.ChangedSince.HasValue && !supportsModSeq)
				throw new NotSupportedException ("The ImapFolder does not support mod-sequences.");

			CheckState (true, false);
			CheckAllowIndexes ();

			return Count > 0 && !IsEmptyFetchRequest (request);
		}

		ImapCommand QueueFetchCommand (int min, int max, IFetchRequest request, CancellationToken cancellationToken, out bool previewText)
		{
			var query = FormatSummaryItems (Engine, request, out previewText);
			int capacity = (max == -1 || max > Count ? Count : max) - min;
			var set = GetFetchRange (min, max);
			var changedSince = string.Empty;

			if (request.ChangedSince.HasValue)
				changedSince = string.Format (CultureInfo.InvariantCulture, " (CHANGEDSINCE {0})", request.ChangedSince.Value);

			var command = string.Format ("FETCH {0} {1}{2}\r\n", set, query, changedSince);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchSummaryContext (capacity);

			ic.RegisterUntaggedHandler ("FETCH", FetchSummaryItemsAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			return ic;
		}

		/// <summary>
		/// Fetches the message summaries for the messages between the two indexes, inclusive.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the messages between the two
		/// indexes, inclusive.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="min">The minimum index.</param>
		/// <param name="max">The maximum index, or <c>-1</c> to specify no upper bound.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="min"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="max"/> is out of range.</para>
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="request"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override IList<IMessageSummary> Fetch (int min, int max, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (min, max, request))
				return Array.Empty<IMessageSummary> ();

			var ic = QueueFetchCommand (min, max, request, cancellationToken, out bool previewText);
			var ctx = (FetchSummaryContext) ic.UserData;

			Engine.Run (ic);

			ProcessResponseCodes (ic, null);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("FETCH", ic);

			if (previewText)
				GetPreviewText (ctx, cancellationToken);

			return ctx.Messages.AsReadOnly ();
		}

		/// <summary>
		/// Asynchronously fetches the message summaries for the messages between the two indexes, inclusive.
		/// </summary>
		/// <remarks>
		/// <para>Fetches the message summaries for the messages between the two
		/// indexes, inclusive.</para>
		/// <para>It should be noted that if another client has modified any message
		/// in the folder, the IMAP server may choose to return information that was
		/// not explicitly requested. It is therefore important to be prepared to
		/// handle both additional fields on a <see cref="IMessageSummary"/> for
		/// messages that were requested as well as summaries for messages that were
		/// not requested at all.</para>
		/// </remarks>
		/// <returns>An enumeration of summaries for the requested messages.</returns>
		/// <param name="min">The minimum index.</param>
		/// <param name="max">The maximum index, or <c>-1</c> to specify no upper bound.</param>
		/// <param name="request">The fetch request.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="min"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="max"/> is out of range.</para>
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="request"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.NotSupportedException">
		/// The <see cref="ImapFolder"/> does not support mod-sequences.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override async Task<IList<IMessageSummary>> FetchAsync (int min, int max, IFetchRequest request, CancellationToken cancellationToken = default)
		{
			if (!CheckCanFetch (min, max, request))
				return Array.Empty<IMessageSummary> ();

			var ic = QueueFetchCommand (min, max, request, cancellationToken, out bool previewText);
			var ctx = (FetchSummaryContext) ic.UserData;

			await Engine.RunAsync (ic).ConfigureAwait (false);

			ProcessResponseCodes (ic, null);

			if (ic.Response != ImapCommandResponse.Ok)
				throw ImapCommandException.Create ("FETCH", ic);

			if (previewText)
				await GetPreviewTextAsync (ctx, cancellationToken).ConfigureAwait (false);

			return ctx.Messages.AsReadOnly ();
		}

		/// <summary>
		/// Create a backing stream for use with the GetMessage, GetBodyPart, and GetStream methods.
		/// </summary>
		/// <remarks>
		/// <para>Allows subclass implementations to override the type of stream
		/// created for use with the GetMessage, GetBodyPart and GetStream methods.</para>
		/// <para>This could be useful for subclass implementations that intend to implement
		/// support for caching and/or for subclass implementations that want to use
		/// temporary file streams instead of memory-based streams for larger amounts of
		/// message data.</para>
		/// <para>Subclasses that implement caching using this API should wait for
		/// <see cref="CommitStream"/> before adding the stream to their cache.</para>
		/// <para>Streams returned by this method SHOULD clean up any allocated resources
		/// such as deleting temporary files from the file system.</para>
		/// <note type="note">The <paramref name="uid"/> will not be available for the various
		/// GetMessage(), GetBodyPart() and GetStream() methods that take a message index rather
		/// than a <see cref="UniqueId"/>. It may also not be available if the IMAP server
		/// response does not specify the <c>UID</c> value prior to sending the <c>literal-string</c>
		/// token containing the message stream.</note>
		/// </remarks>
		/// <seealso cref="CommitStream"/>
		/// <returns>The stream.</returns>
		/// <param name="uid">The unique identifier of the message, if available.</param>
		/// <param name="section">The section of the message that is being fetched.</param>
		/// <param name="offset">The starting offset of the message section being fetched.</param>
		/// <param name="length">The length of the stream being fetched, measured in bytes.</param>
		protected virtual Stream CreateStream (UniqueId? uid, string section, int offset, int length)
		{
			if (length > 4096)
				return new MemoryBlockStream ();

			return new MemoryStream (length);
		}

		/// <summary>
		/// Commit a stream returned by <see cref="CreateStream"/>.
		/// </summary>
		/// <remarks>
		/// <para>Commits a stream returned by <see cref="CreateStream"/>.</para>
		/// <para>This method is called only after both the message data has successfully
		/// been written to the stream returned by <see cref="CreateStream"/> and a
		/// <see cref="UniqueId"/> has been obtained for the associated message.</para>
		/// <para>For subclasses implementing caching, this method should be used for
		/// committing the stream to their cache.</para>
		/// <note type="note">Subclass implementations may take advantage of the fact that
		/// <see cref="CommitStream"/> allows returning a new <see cref="System.IO.Stream"/>
		/// reference if they move a file on the file system and wish to return a new
		/// <see cref="System.IO.FileStream"/> based on the new path, for example.</note>
		/// </remarks>
		/// <seealso cref="CreateStream"/>
		/// <returns>The stream.</returns>
		/// <param name="stream">The stream.</param>
		/// <param name="uid">The unique identifier of the message.</param>
		/// <param name="section">The section of the message that the stream represents.</param>
		/// <param name="offset">The starting offset of the message section.</param>
		/// <param name="length">The length of the stream, measured in bytes.</param>
		protected virtual Stream CommitStream (Stream stream, UniqueId uid, string section, int offset, int length)
		{
			return stream;
		}

		async Task<HeaderList> ParseHeadersAsync (Stream stream, bool doAsync, CancellationToken cancellationToken)
		{
			try {
				return await Engine.ParseHeadersAsync (stream, doAsync, cancellationToken).ConfigureAwait (false);
			} finally {
				stream.Dispose ();
			}
		}

		async Task<MimeMessage> ParseMessageAsync (Stream stream, bool doAsync, CancellationToken cancellationToken)
		{
			bool dispose = !(stream is MemoryStream || stream is MemoryBlockStream);

			try {
				return await Engine.ParseMessageAsync (stream, !dispose, doAsync, cancellationToken).ConfigureAwait (false);
			} finally {
				if (dispose)
					stream.Dispose ();
			}
		}

		async Task<MimeEntity> ParseEntityAsync (Stream stream, bool dispose, bool doAsync, CancellationToken cancellationToken)
		{
			try {
				return await Engine.ParseEntityAsync (stream, !dispose, doAsync, cancellationToken).ConfigureAwait (false);
			} finally {
				if (dispose)
					stream.Dispose ();
			}
		}

		class Section
		{
			public int Index;
			public UniqueId? UniqueId;
			public Stream Stream;
			public string Name;
			public int Offset;
			public int Length;

			public Section (Stream stream, int index, UniqueId? uid, string name, int offset, int length)
			{
				Stream = stream;
				Offset = offset;
				Length = length;
				UniqueId = uid;
				Index = index;
				Name = name;
			}
		}

		abstract class FetchStreamContextBase : IDisposable
		{
			public readonly List<Section> Sections = new List<Section> ();
			readonly ITransferProgress progress;

			public FetchStreamContextBase (ITransferProgress progress)
			{
				this.progress = progress;
			}

			public abstract Task AddAsync (Section section, bool doAsync, CancellationToken cancellationToken);

			public virtual bool Contains (int index, string specifier, out Section section)
			{
				section = null;
				return false;
			}

			public abstract Task SetUniqueIdAsync (int index, UniqueId uid, bool doAsync, CancellationToken cancellationToken);

			public void Report (long nread, long total)
			{
				if (progress == null)
					return;

				progress.Report (nread, total);
			}

			public void Dispose ()
			{
				for (int i = 0; i < Sections.Count; i++) {
					var section = Sections[i];

					try {
						section.Stream.Dispose ();
					} catch (IOException) {
					}
				}
			}
		}

		class FetchStreamContext : FetchStreamContextBase
		{
			public FetchStreamContext (ITransferProgress progress) : base (progress)
			{
			}

			public override Task AddAsync (Section section, bool doAsync, CancellationToken cancellationToken)
			{
				Sections.Add (section);
				return Task.CompletedTask;
			}

			public bool TryGetSection (UniqueId uid, string specifier, out Section section, bool remove = false)
			{
				for (int i = 0; i < Sections.Count; i++) {
					var item = Sections[i];

					if (!item.UniqueId.HasValue || item.UniqueId.Value != uid)
						continue;

					if (item.Name.Equals (specifier, StringComparison.OrdinalIgnoreCase)) {
						if (remove)
							Sections.RemoveAt (i);

						section = item;
						return true;
					}
				}

				section = null;

				return false;
			}

			public bool TryGetSection (int index, string specifier, out Section section, bool remove = false)
			{
				for (int i = 0; i < Sections.Count; i++) {
					var item = Sections[i];

					if (item.Index != index)
						continue;

					if (item.Name.Equals (specifier, StringComparison.OrdinalIgnoreCase)) {
						if (remove)
							Sections.RemoveAt (i);

						section = item;
						return true;
					}
				}

				section = null;

				return false;
			}

			public override Task SetUniqueIdAsync (int index, UniqueId uid, bool doAsync, CancellationToken cancellationToken)
			{
				for (int i = 0; i < Sections.Count; i++) {
					if (Sections[i].Index == index)
						Sections[i].UniqueId = uid;
				}

				return Task.CompletedTask;
			}
		}

		async Task FetchStreamAsync (ImapEngine engine, ImapCommand ic, int index, bool doAsync)
		{
			var token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);
			var annotations = new AnnotationsChangedEventArgs (index);
			var labels = new MessageLabelsChangedEventArgs (index);
			var flags = new MessageFlagsChangedEventArgs (index);
			var modSeq = new ModSeqChangedEventArgs (index);
			var ctx = (FetchStreamContextBase) ic.UserData;
			var sectionBuilder = new StringBuilder ();
			bool annotationsChanged = false;
			bool modSeqChanged = false;
			bool labelsChanged = false;
			bool flagsChanged = false;
			long nread = 0, size = 0;
			UniqueId? uid = null;
			Stream stream;
			string name;
			byte[] buf;
			int n;

			ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			do {
				token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

				if (token.Type == ImapTokenType.Eoln) {
					// Note: Most likely the the message body was calculated to be 1 or 2 bytes too
					// short (e.g. did not include the trailing <CRLF>) and that is the EOLN we just
					// reached. Ignore it and continue as normal.
					//
					// See https://github.com/jstedfast/MailKit/issues/954 for details.
					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);
				}

				if (token.Type == ImapTokenType.CloseParen)
					break;

				ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

				var atom = (string) token.Value;
				int offset = 0, length;
				ulong modseq;
				uint value;

				if (atom.Equals ("BODY", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.OpenBracket, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					sectionBuilder.Clear ();

					do {
						token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

						if (token.Type == ImapTokenType.CloseBracket)
							break;

						if (token.Type == ImapTokenType.OpenParen) {
							sectionBuilder.Append (" (");

							do {
								token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

								if (token.Type == ImapTokenType.CloseParen)
									break;

								// the header field names will generally be atoms or qstrings but may also be literals
								switch (token.Type) {
								case ImapTokenType.Literal:
									sectionBuilder.Append (await engine.ReadLiteralAsync (doAsync, ic.CancellationToken).ConfigureAwait (false));
									break;
								case ImapTokenType.QString:
								case ImapTokenType.Atom:
									sectionBuilder.Append ((string) token.Value);
									break;
								default:
									throw ImapEngine.UnexpectedToken (ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
								}

								sectionBuilder.Append (' ');
							} while (true);

							if (sectionBuilder[sectionBuilder.Length - 1] == ' ')
								sectionBuilder.Length--;

							sectionBuilder.Append (')');
						} else {
							ImapEngine.AssertToken (token, ImapTokenType.Atom, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

							sectionBuilder.Append ((string) token.Value);
						}
					} while (true);

					ImapEngine.AssertToken (token, ImapTokenType.CloseBracket, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					if (token.Type == ImapTokenType.Atom) {
						// this might be a region ("<###>")
						var expr = (string) token.Value;

						if (expr.Length > 2 && expr[0] == '<' && expr[expr.Length - 1] == '>') {
							var region = expr.Substring (1, expr.Length - 2);

							int.TryParse (region, NumberStyles.None, CultureInfo.InvariantCulture, out offset);

							token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);
						}
					}

					name = sectionBuilder.ToString ();

					switch (token.Type) {
					case ImapTokenType.Literal:
						length = (int) token.Value;
						size += length;

						stream = CreateStream (uid, name, offset, length);

						buf = ArrayPool<byte>.Shared.Rent (BufferSize);

						try {
							do {
								if (doAsync)
									n = await engine.Stream.ReadAsync (buf, 0, BufferSize, ic.CancellationToken).ConfigureAwait (false);
								else
									n = engine.Stream.Read (buf, 0, BufferSize, ic.CancellationToken);

								if (n > 0) {
									stream.Write (buf, 0, n);
									nread += n;

									ctx.Report (nread, size);
								} else {
									break;
								}
							} while (true);

							stream.Position = 0;
						} catch {
							stream.Dispose ();
							throw;
						} finally {
							ArrayPool<byte>.Shared.Return (buf);
						}
						break;
					case ImapTokenType.QString:
					case ImapTokenType.Atom:
						buf = Encoding.UTF8.GetBytes ((string) token.Value);
						length = buf.Length;
						nread += length;
						size += length;

						stream = CreateStream (uid, name, offset, length);

						try {
							stream.Write (buf, 0, length);
							ctx.Report (nread, size);
							stream.Position = 0;
						} catch {
							stream.Dispose ();
							throw;
						}
						break;
					case ImapTokenType.Nil:
						stream = CreateStream (uid, name, offset, 0);
						length = 0;
						break;
					default:
						throw ImapEngine.UnexpectedToken (ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					}

					if (uid.HasValue)
						stream = CommitStream (stream, uid.Value, name, offset, length);

					// prevent leaks in the (invalid) case where a section may be returned twice
					if (ctx.Contains (index, name, out var section))
						section.Stream.Dispose ();

					section = new Section (stream, index, uid, name, offset, length);
					await ctx.AddAsync (section, doAsync, ic.CancellationToken).ConfigureAwait (false);
				} else if (atom.Equals ("UID", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					value = ImapEngine.ParseNumber (token, true, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);
					uid = new UniqueId (UidValidity, value);

					await ctx.SetUniqueIdAsync (index, uid.Value, doAsync, ic.CancellationToken).ConfigureAwait (false);

					annotations.UniqueId = uid.Value;
					modSeq.UniqueId = uid.Value;
					labels.UniqueId = uid.Value;
					flags.UniqueId = uid.Value;
				} else if (atom.Equals ("MODSEQ", StringComparison.OrdinalIgnoreCase)) {
					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.OpenParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					modseq = ImapEngine.ParseNumber64 (token, false, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericItemSyntaxErrorFormat, atom, token);

					if (modseq > HighestModSeq)
						UpdateHighestModSeq (modseq);

					annotations.ModSeq = modseq;
					modSeq.ModSeq = modseq;
					labels.ModSeq = modseq;
					flags.ModSeq = modseq;
					modSeqChanged = true;
				} else if (atom.Equals ("BODY", StringComparison.OrdinalIgnoreCase)) {
					// even though we didn't request this piece of information, the IMAP server
					// may send it if another client has recently modified the message flags.
					flags.Flags = await ImapUtils.ParseFlagsListAsync (engine, atom, (HashSet<string>) flags.Keywords, doAsync, ic.CancellationToken).ConfigureAwait (false);
					flagsChanged = true;
				} else if (atom.Equals ("X-GM-LABELS", StringComparison.OrdinalIgnoreCase)) {
					// even though we didn't request this piece of information, the IMAP server
					// may send it if another client has recently modified the message labels.
					labels.Labels = await ImapUtils.ParseLabelsListAsync (engine, doAsync, ic.CancellationToken).ConfigureAwait (false);
					labelsChanged = true;
				} else if (atom.Equals ("ANNOTATION", StringComparison.OrdinalIgnoreCase)) {
					// even though we didn't request this piece of information, the IMAP server
					// may send it if another client has recently modified the message annotations.
					annotations.Annotations = await ImapUtils.ParseAnnotationsAsync (engine, doAsync, ic.CancellationToken).ConfigureAwait (false);
					annotationsChanged = true;
				} else {
					// Unexpected or unknown token (such as XAOL.SPAM.REASON or XAOL-MSGID). Simply read 1 more token (the argument) and ignore.
					token = await engine.ReadTokenAsync (doAsync, ic.CancellationToken).ConfigureAwait (false);

					if (token.Type == ImapTokenType.OpenParen)
						await SkipParenthesizedListAsync (engine, doAsync, ic.CancellationToken).ConfigureAwait (false);
				}
			} while (true);

			ImapEngine.AssertToken (token, ImapTokenType.CloseParen, ImapEngine.GenericUntaggedResponseSyntaxErrorFormat, "FETCH", token);

			if (flagsChanged)
				OnMessageFlagsChanged (flags);

			if (labelsChanged)
				OnMessageLabelsChanged (labels);

			if (annotationsChanged)
				OnAnnotationsChanged (annotations);

			if (modSeqChanged)
				OnModSeqChanged (modSeq);
		}

		static string GetBodyPartQuery (string partSpec, bool headersOnly, out string[] tags)
		{
			string query;

			if (headersOnly) {
				tags = new string[1];

				if (partSpec.Length > 0) {
					query = string.Format ("BODY.PEEK[{0}.MIME]", partSpec);
					tags[0] = partSpec + ".MIME";
				} else {
					query = "BODY.PEEK[HEADER]";
					tags[0] = "HEADER";
				}
			} else {
				tags = new string[2];

				if (partSpec.Length > 0) {
					tags[0] = partSpec + ".MIME";
					tags[1] = partSpec;
				} else {
					tags[0] = "HEADER";
					tags[1] = "TEXT";
				}

				query = string.Format ("BODY.PEEK[{0}] BODY.PEEK[{1}]", tags[0], tags[1]);
			}

			return query;
		}

		async Task<HeaderList> GetHeadersAsync (UniqueId uid, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			CheckState (true, false);

			var ic = new ImapCommand (Engine, cancellationToken, this, "UID FETCH %u (BODY.PEEK[HEADER])\r\n", uid.Id);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, "HEADER", out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested message headers.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseHeadersAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		async Task<HeaderList> GetHeadersAsync (UniqueId uid, string partSpecifier, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (partSpecifier == null)
				throw new ArgumentNullException (nameof (partSpecifier));

			CheckState (true, false);

			var command = string.Format ("UID FETCH {0} ({1})\r\n", uid, GetBodyPartQuery (partSpecifier, true, out var tags));
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, tags[0], out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested body part headers.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseHeadersAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		/// <summary>
		/// Get the specified message headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified message headers.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override HeaderList GetHeaders (UniqueId uid, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (uid, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified message headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified message headers.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<HeaderList> GetHeadersAsync (UniqueId uid, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (uid, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual HeaderList GetHeaders (UniqueId uid, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (uid, partSpecifier, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task<HeaderList> GetHeadersAsync (UniqueId uid, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (uid, partSpecifier, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override HeaderList GetHeaders (UniqueId uid, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetHeaders (uid, part.PartSpecifier, cancellationToken, progress);
		}

		/// <summary>
		/// Asynchronously get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<HeaderList> GetHeadersAsync (UniqueId uid, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetHeadersAsync (uid, part.PartSpecifier, cancellationToken, progress);
		}

		async Task<HeaderList> GetHeadersAsync (int index, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			CheckState (true, false);

			var ic = new ImapCommand (Engine, cancellationToken, this, "FETCH %d (BODY.PEEK[HEADER])\r\n", index + 1);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, "HEADER", out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested message.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseHeadersAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		async Task<HeaderList> GetHeadersAsync (int index, string partSpecifier, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (partSpecifier == null)
				throw new ArgumentNullException (nameof (partSpecifier));

			CheckState (true, false);

			var seqid = (index + 1).ToString (CultureInfo.InvariantCulture);
			var command = string.Format ("FETCH {0} ({1})\r\n", seqid, GetBodyPartQuery (partSpecifier, true, out var tags));
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, tags[0], out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested body part headers.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseHeadersAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		/// <summary>
		/// Get the specified message headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified message headers.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override HeaderList GetHeaders (int index, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (index, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified message headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified message headers.
		/// </remarks>
		/// <returns>The message headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<HeaderList> GetHeadersAsync (int index, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (index, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual HeaderList GetHeaders (int index, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (index, partSpecifier, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task<HeaderList> GetHeadersAsync (int index, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetHeadersAsync (index, partSpecifier, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override HeaderList GetHeaders (int index, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetHeaders (index, part.PartSpecifier, cancellationToken, progress);
		}

		/// <summary>
		/// Asynchronously get the specified body part headers.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part headers.
		/// </remarks>
		/// <returns>The body part headers.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested body part headers.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<HeaderList> GetHeadersAsync (int index, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetHeadersAsync (index, part.PartSpecifier, cancellationToken, progress);
		}

		async Task<MimeMessage> GetMessageAsync (UniqueId uid, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			CheckState (true, false);

			var ic = new ImapCommand (Engine, cancellationToken, this, "UID FETCH %u (BODY.PEEK[])\r\n", uid.Id);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, string.Empty, out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested message.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseMessageAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		/// <summary>
		/// Get the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the specified message.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override MimeMessage GetMessage (UniqueId uid, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetMessageAsync (uid, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the specified message.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<MimeMessage> GetMessageAsync (UniqueId uid, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetMessageAsync (uid, true, cancellationToken, progress);
		}

		async Task<MimeMessage> GetMessageAsync (int index, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			CheckState (true, false);

			var ic = new ImapCommand (Engine, cancellationToken, this, "FETCH %d (BODY.PEEK[])\r\n", index + 1);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, string.Empty, out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested message.");
			} finally {
				ctx.Dispose ();
			}

			return await ParseMessageAsync (section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
		}

		/// <summary>
		/// Get the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the specified message.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override MimeMessage GetMessage (int index, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetMessageAsync (index, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified message.
		/// </summary>
		/// <remarks>
		/// Gets the specified message.
		/// </remarks>
		/// <returns>The message.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<MimeMessage> GetMessageAsync (int index, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetMessageAsync (index, true, cancellationToken, progress);
		}

		async Task<MimeEntity> GetBodyPartAsync (UniqueId uid, string partSpecifier, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (partSpecifier == null)
				throw new ArgumentNullException (nameof (partSpecifier));

			CheckState (true, false);

			var command = string.Format ("UID FETCH {0} ({1})\r\n", uid, GetBodyPartQuery (partSpecifier, false, out var tags));
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			ChainedStream chained = null;
			bool dispose = false;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				chained = new ChainedStream ();

				foreach (var tag in tags) {
					if (!ctx.TryGetSection (uid, tag, out var section, true))
						throw new MessageNotFoundException ("The IMAP server did not return the requested body part.");

					if (!(section.Stream is MemoryStream || section.Stream is MemoryBlockStream))
						dispose = true;

					chained.Add (section.Stream);
				}
			} catch {
				chained?.Dispose ();
				throw;
			} finally {
				ctx.Dispose ();
			}

			var entity = await ParseEntityAsync (chained, dispose, doAsync, cancellationToken).ConfigureAwait (false);

			if (partSpecifier.Length == 0) {
				for (int i = entity.Headers.Count; i > 0; i--) {
					var header = entity.Headers[i - 1];

					if (!header.Field.StartsWith ("Content-", StringComparison.OrdinalIgnoreCase))
						entity.Headers.RemoveAt (i - 1);
				}
			}

			return entity;
		}

		/// <summary>
		/// Get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueIdAndSpecifier"/>
		/// </example>
		/// <returns>The body part.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message body.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual MimeEntity GetBodyPart (UniqueId uid, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetBodyPartAsync (uid, partSpecifier, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueIdAndSpecifier"/>
		/// </example>
		/// <returns>The body part.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message body.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task<MimeEntity> GetBodyPartAsync (UniqueId uid, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetBodyPartAsync (uid, partSpecifier, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueId"/>
		/// </example>
		/// <returns>The body part.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message body.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override MimeEntity GetBodyPart (UniqueId uid, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetBodyPart (uid, part.PartSpecifier, cancellationToken, progress);
		}

		/// <summary>
		/// Asynchronously get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartsByUniqueId"/>
		/// </example>
		/// <returns>The body part.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message body.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<MimeEntity> GetBodyPartAsync (UniqueId uid, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetBodyPartAsync (uid, part.PartSpecifier, cancellationToken, progress);
		}

		async Task<MimeEntity> GetBodyPartAsync (int index, string partSpecifier, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (partSpecifier == null)
				throw new ArgumentNullException (nameof (partSpecifier));

			CheckState (true, false);

			var seqid = (index + 1).ToString (CultureInfo.InvariantCulture);
			var command = string.Format ("FETCH {0} ({1})\r\n", seqid, GetBodyPartQuery (partSpecifier, false, out var tags));
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			ChainedStream chained = null;
			bool dispose = false;
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				chained = new ChainedStream ();

				foreach (var tag in tags) {
					if (!ctx.TryGetSection (index, tag, out section, true))
						throw new MessageNotFoundException ("The IMAP server did not return the requested body part.");

					if (!(section.Stream is MemoryStream || section.Stream is MemoryBlockStream))
						dispose = true;

					chained.Add (section.Stream);
				}
			} catch {
				chained?.Dispose ();
				throw;
			} finally {
				ctx.Dispose ();
			}

			var entity = await ParseEntityAsync (chained, dispose, doAsync, cancellationToken).ConfigureAwait (false);

			if (partSpecifier.Length == 0) {
				for (int i = entity.Headers.Count; i > 0; i--) {
					var header = entity.Headers[i - 1];

					if (!header.Field.StartsWith ("Content-", StringComparison.OrdinalIgnoreCase))
						entity.Headers.RemoveAt (i - 1);
				}
			}

			return entity;
		}

		/// <summary>
		/// Get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <returns>The body part.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual MimeEntity GetBodyPart (int index, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetBodyPartAsync (index, partSpecifier, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <returns>The body part.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="partSpecifier">The body part specifier.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="partSpecifier"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task<MimeEntity> GetBodyPartAsync (int index, string partSpecifier, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetBodyPartAsync (index, partSpecifier, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <returns>The body part.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override MimeEntity GetBodyPart (int index, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetBodyPart (index, part.PartSpecifier, cancellationToken, progress);
		}

		/// <summary>
		/// Asynchronously get the specified body part.
		/// </summary>
		/// <remarks>
		/// Gets the specified body part.
		/// </remarks>
		/// <returns>The body part.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="part">The body part.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="part"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<MimeEntity> GetBodyPartAsync (int index, BodyPart part, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (part == null)
				throw new ArgumentNullException (nameof (part));

			return GetBodyPartAsync (index, part.PartSpecifier, cancellationToken, progress);
		}

		async Task<Stream> GetStreamAsync (UniqueId uid, int offset, int count, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));

			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count));

			CheckState (true, false);

			if (count == 0)
				return new MemoryStream ();

			var ic = new ImapCommand (Engine, cancellationToken, this, "UID FETCH %u (BODY.PEEK[]<%d.%d>)\r\n", uid.Id, offset, count);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, string.Empty, out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return section.Stream;
		}

		async Task<Stream> GetStreamAsync (int index, int offset, int count, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));

			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count));

			CheckState (true, false);

			if (count == 0)
				return new MemoryStream ();

			var ic = new ImapCommand (Engine, cancellationToken, this, "FETCH %d (BODY.PEEK[]<%d.%d>)\r\n", index + 1, offset, count);
			var ctx = new FetchStreamContext (progress);
			Section section;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, string.Empty, out section, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return section.Stream;
		}

		/// <summary>
		/// Get a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// Fetches a substream of the message. If the starting offset is beyond
		/// the end of the message, an empty stream is returned. If the number of
		/// bytes desired extends beyond the end of the message, the stream will
		/// end where the message ends.
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (UniqueId uid, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, offset, count, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// Fetches a substream of the message. If the starting offset is beyond
		/// the end of the message, an empty stream is returned. If the number of
		/// bytes desired extends beyond the end of the message,  the stream will
		/// end where the message ends.
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (UniqueId uid, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, offset, count, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// Fetches a substream of the message. If the starting offset is beyond
		/// the end of the message, an empty stream is returned. If the number of
		/// bytes desired extends beyond the end of the message, a truncated stream
		/// will be returned.
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="index"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (int index, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, offset, count, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// Fetches a substream of the message. If the starting offset is beyond
		/// the end of the message, an empty stream is returned. If the number of
		/// bytes desired extends beyond the end of the message, a truncated stream
		/// will be returned.
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="index"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (int index, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, offset, count, true, cancellationToken, progress);
		}

		async Task<Stream> GetStreamAsync (UniqueId uid, string section, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (section == null)
				throw new ArgumentNullException (nameof (section));

			CheckState (true, false);

			var command = string.Format ("UID FETCH {0} (BODY.PEEK[{1}])\r\n", uid, section);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section s;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, section, out s, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return s.Stream;
		}

		/// <summary>
		/// Get a substream of the specified body part.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartStreamsByUniqueIdAndSpecifier"/>
		/// </example>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (UniqueId uid, string section, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, section, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified body part.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <example>
		/// <code language="c#" source="Examples\ImapBodyPartExamples.cs" region="GetBodyPartStreamsByUniqueIdAndSpecifier"/>
		/// </example>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (UniqueId uid, string section, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, section, true, cancellationToken, progress);
		}

		async Task<Stream> GetStreamAsync (UniqueId uid, string section, int offset, int count, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (!uid.IsValid)
				throw new ArgumentException ("The uid is invalid.", nameof (uid));

			if (section == null)
				throw new ArgumentNullException (nameof (section));

			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));

			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count));

			CheckState (true, false);

			if (count == 0)
				return new MemoryStream ();

			var range = string.Format (CultureInfo.InvariantCulture, "{0}.{1}", offset, count);
			var command = string.Format ("UID FETCH {0} (BODY.PEEK[{1}]<{2}>)\r\n", uid, section, range);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section s;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (uid, section, out s, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return s.Stream;
		}

		/// <summary>
		/// Get a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message. If the starting offset is beyond
		/// the end of the specified section of the message, an empty stream is returned. If
		/// the number of bytes desired extends beyond the end of the section, a truncated
		/// stream will be returned.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (UniqueId uid, string section, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, section, offset, count, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message. If the starting offset is beyond
		/// the end of the specified section of the message, an empty stream is returned. If
		/// the number of bytes desired extends beyond the end of the section, a truncated
		/// stream will be returned.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="uid">The UID of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentException">
		/// <paramref name="uid"/> is invalid.
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (UniqueId uid, string section, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (uid, section, offset, count, true, cancellationToken, progress);
		}

		async Task<Stream> GetStreamAsync (int index, string section, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (section == null)
				throw new ArgumentNullException (nameof (section));

			CheckState (true, false);

			var seqid = (index + 1).ToString (CultureInfo.InvariantCulture);
			var command = string.Format ("FETCH {0} (BODY.PEEK[{1}])\r\n", seqid, section);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section s;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, section, out s, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return s.Stream;
		}

		/// <summary>
		/// Get a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (int index, string section, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, section, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <paramref name="index"/> is out of range.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (int index, string section, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, section, true, cancellationToken, progress);
		}

		async Task<Stream> GetStreamAsync (int index, string section, int offset, int count, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (index < 0 || index >= Count)
				throw new ArgumentOutOfRangeException (nameof (index));

			if (section == null)
				throw new ArgumentNullException (nameof (section));

			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));

			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count));

			CheckState (true, false);

			if (count == 0)
				return new MemoryStream ();

			var seqid = (index + 1).ToString (CultureInfo.InvariantCulture);
			var range = string.Format (CultureInfo.InvariantCulture, "{0}.{1}", offset, count);
			var command = string.Format ("FETCH {0} (BODY.PEEK[{1}]<{2}>)\r\n", seqid, section, range);
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamContext (progress);
			Section s;

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);

				if (!ctx.TryGetSection (index, section, out s, true))
					throw new MessageNotFoundException ("The IMAP server did not return the requested stream.");
			} finally {
				ctx.Dispose ();
			}

			return s.Stream;
		}

		/// <summary>
		/// Get a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message. If the starting offset is beyond
		/// the end of the specified section of the message, an empty stream is returned. If
		/// the number of bytes desired extends beyond the end of the section, a truncated
		/// stream will be returned.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="index"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Stream GetStream (int index, string section, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, section, offset, count, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously gets a substream of the specified message.
		/// </summary>
		/// <remarks>
		/// <para>Gets a substream of the specified message. If the starting offset is beyond
		/// the end of the specified section of the message, an empty stream is returned. If
		/// the number of bytes desired extends beyond the end of the section, a truncated
		/// stream will be returned.</para>
		/// <para>For more information about how to construct the <paramref name="section"/>,
		/// see Section 6.4.5 of RFC3501.</para>
		/// </remarks>
		/// <returns>The stream.</returns>
		/// <param name="index">The index of the message.</param>
		/// <param name="section">The desired section of the message.</param>
		/// <param name="offset">The starting offset of the first desired byte.</param>
		/// <param name="count">The number of bytes desired.</param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="section"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="index"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="offset"/> is negative.</para>
		/// <para>-or-</para>
		/// <para><paramref name="count"/> is negative.</para>
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="MessageNotFoundException">
		/// The IMAP server did not return the requested message stream.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public override Task<Stream> GetStreamAsync (int index, string section, int offset, int count, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamAsync (index, section, offset, count, true, cancellationToken, progress);
		}

		class FetchStreamCallbackContext : FetchStreamContextBase
		{
			readonly ImapFolder folder;
			readonly object callback;

			public FetchStreamCallbackContext (ImapFolder folder, object callback, ITransferProgress progress) : base (progress)
			{
				this.folder = folder;
				this.callback = callback;
			}

			Task InvokeCallbackAsync (ImapFolder folder, int index, UniqueId uid, Stream stream, bool doAsync, CancellationToken cancellationToken)
			{
				if (doAsync)
					return ((ImapFetchStreamAsyncCallback) callback) (folder, index, uid, stream, cancellationToken);

				((ImapFetchStreamCallback) callback) (folder, index, uid, stream);
				return Task.CompletedTask;
			}

			public override async Task AddAsync (Section section, bool doAsync, CancellationToken cancellationToken)
			{
				if (section.UniqueId.HasValue) {
					await InvokeCallbackAsync (folder, section.Index, section.UniqueId.Value, section.Stream, doAsync, cancellationToken).ConfigureAwait (false);
					section.Stream.Dispose ();
				} else {
					Sections.Add (section);
				}
			}

			public override async Task SetUniqueIdAsync (int index, UniqueId uid, bool doAsync, CancellationToken cancellationToken)
			{
				for (int i = 0; i < Sections.Count; i++) {
					if (Sections[i].Index == index) {
						await InvokeCallbackAsync (folder, index, uid, Sections[i].Stream, doAsync, cancellationToken).ConfigureAwait (false);
						Sections[i].Stream.Dispose ();
						Sections.RemoveAt (i);
						break;
					}
				}
			}
		}

		async Task GetStreamsAsync (IList<UniqueId> uids, object callback, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (uids == null)
				throw new ArgumentNullException (nameof (uids));

			if (callback == null)
				throw new ArgumentNullException (nameof (callback));

			CheckState (true, false);

			if (uids.Count == 0)
				return;

			var ctx = new FetchStreamCallbackContext (this, callback, progress);
			var command = "UID FETCH %s (BODY.PEEK[])\r\n";

			try {
				foreach (var ic in Engine.CreateCommands (cancellationToken, this, command, uids)) {
					ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
					ic.UserData = ctx;

					Engine.QueueCommand (ic);

					await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

					ProcessResponseCodes (ic, null);

					if (ic.Response != ImapCommandResponse.Ok)
						throw ImapCommandException.Create ("FETCH", ic);
				}
			} finally {
				ctx.Dispose ();
			}
		}

		async Task GetStreamsAsync (IList<int> indexes, object callback, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (indexes == null)
				throw new ArgumentNullException (nameof (indexes));

			if (callback == null)
				throw new ArgumentNullException (nameof (callback));

			CheckState (true, false);
			CheckAllowIndexes ();

			if (indexes.Count == 0)
				return;

			var command = new StringBuilder ("FETCH ");
			ImapUtils.FormatIndexSet (Engine, command, indexes);
			command.Append (" (UID BODY.PEEK[])\r\n");

			var ic = new ImapCommand (Engine, cancellationToken, this, command.ToString ());
			var ctx = new FetchStreamCallbackContext (this, callback, progress);

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);
			} finally {
				ctx.Dispose ();
			}
		}

		async Task GetStreamsAsync (int min, int max, object callback, bool doAsync, CancellationToken cancellationToken, ITransferProgress progress)
		{
			if (min < 0)
				throw new ArgumentOutOfRangeException (nameof (min));

			if (max != -1 && max < min)
				throw new ArgumentOutOfRangeException (nameof (max));

			if (callback == null)
				throw new ArgumentNullException (nameof (callback));

			CheckState (true, false);
			CheckAllowIndexes ();

			if (min == Count)
				return;

			var command = string.Format ("FETCH {0} (UID BODY.PEEK[])\r\n", GetFetchRange (min, max));
			var ic = new ImapCommand (Engine, cancellationToken, this, command);
			var ctx = new FetchStreamCallbackContext (this, callback, progress);

			ic.RegisterUntaggedHandler ("FETCH", FetchStreamAsync);
			ic.UserData = ctx;

			Engine.QueueCommand (ic);

			try {
				await Engine.RunAsync (ic, doAsync).ConfigureAwait (false);

				ProcessResponseCodes (ic, null);

				if (ic.Response != ImapCommandResponse.Ok)
					throw ImapCommandException.Create ("FETCH", ic);
			} finally {
				ctx.Dispose ();
			}
		}

		/// <summary>
		/// Get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Gets the streams for the specified messages.</para>
		/// </remarks>
		/// <param name="uids">The uids of the messages.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="uids"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="callback"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="uids"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual void GetStreams (IList<UniqueId> uids, ImapFetchStreamCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			GetStreamsAsync (uids, callback, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Asynchronously gets the streams for the specified messages.</para>
		/// </remarks>
		/// <returns>An awaitable task.</returns>
		/// <param name="uids">The uids of the messages.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="uids"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="callback"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="uids"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task GetStreamsAsync (IList<UniqueId> uids, ImapFetchStreamAsyncCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamsAsync (uids, callback, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Gets the streams for the specified messages.</para>
		/// </remarks>
		/// <param name="indexes">The indexes of the messages.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="indexes"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="callback"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="indexes"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual void GetStreams (IList<int> indexes, ImapFetchStreamCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			GetStreamsAsync (indexes, callback, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Asynchronously gets the streams for the specified messages.</para>
		/// </remarks>
		/// <returns>An awaitable task.</returns>
		/// <param name="indexes">The indexes of the messages.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentNullException">
		/// <para><paramref name="indexes"/> is <c>null</c>.</para>
		/// <para>-or-</para>
		/// <para><paramref name="callback"/> is <c>null</c>.</para>
		/// </exception>
		/// <exception cref="System.ArgumentException">
		/// One or more of the <paramref name="indexes"/> is invalid.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task GetStreamsAsync (IList<int> indexes, ImapFetchStreamAsyncCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamsAsync (indexes, callback, true, cancellationToken, progress);
		}

		/// <summary>
		/// Get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Gets the streams for the specified messages.</para>
		/// </remarks>
		/// <param name="min">The minimum index.</param>
		/// <param name="max">The maximum index, or <c>-1</c> to specify no upper bound.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="min"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="max"/> is out of range.</para>
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="callback"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual void GetStreams (int min, int max, ImapFetchStreamCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			GetStreamsAsync (min, max, callback, false, cancellationToken, progress).GetAwaiter ().GetResult ();
		}

		/// <summary>
		/// Asynchronously get the streams for the specified messages.
		/// </summary>
		/// <remarks>
		/// <para>Asynchronously gets the streams for the specified messages.</para>
		/// </remarks>
		/// <returns>An awaitable task.</returns>
		/// <param name="min">The minimum index.</param>
		/// <param name="max">The maximum index, or <c>-1</c> to specify no upper bound.</param>
		/// <param name="callback"></param>
		/// <param name="cancellationToken">The cancellation token.</param>
		/// <param name="progress">The progress reporting mechanism.</param>
		/// <exception cref="System.ArgumentOutOfRangeException">
		/// <para><paramref name="min"/> is out of range.</para>
		/// <para>-or-</para>
		/// <para><paramref name="max"/> is out of range.</para>
		/// </exception>
		/// <exception cref="System.ArgumentNullException">
		/// <paramref name="callback"/> is <c>null</c>.
		/// </exception>
		/// <exception cref="System.ObjectDisposedException">
		/// The <see cref="ImapClient"/> has been disposed.
		/// </exception>
		/// <exception cref="ServiceNotConnectedException">
		/// The <see cref="ImapClient"/> is not connected.
		/// </exception>
		/// <exception cref="ServiceNotAuthenticatedException">
		/// The <see cref="ImapClient"/> is not authenticated.
		/// </exception>
		/// <exception cref="FolderNotOpenException">
		/// The <see cref="ImapFolder"/> is not currently open.
		/// </exception>
		/// <exception cref="System.OperationCanceledException">
		/// The operation was canceled via the cancellation token.
		/// </exception>
		/// <exception cref="System.IO.IOException">
		/// An I/O error occurred.
		/// </exception>
		/// <exception cref="ImapProtocolException">
		/// The server's response contained unexpected tokens.
		/// </exception>
		/// <exception cref="ImapCommandException">
		/// The server replied with a NO or BAD response.
		/// </exception>
		public virtual Task GetStreamsAsync (int min, int max, ImapFetchStreamAsyncCallback callback, CancellationToken cancellationToken = default, ITransferProgress progress = null)
		{
			return GetStreamsAsync (min, max, callback, true, cancellationToken, progress);
		}
	}
}
