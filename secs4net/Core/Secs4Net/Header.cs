﻿using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Secs4Net
{
	public readonly struct MessageHeader
	{
		public readonly ushort DeviceId;
		public readonly bool ReplyExpected;
		public readonly byte S;
		public readonly byte F;
		public readonly MessageType MessageType;
		public readonly int SystemBytes;

		internal MessageHeader(
			ushort deviceId = default,
			bool replyExpected = default,
			byte s = default,
			byte f = default,
			MessageType messageType = default,
			int systemBytes = default)
		{
			this.DeviceId = deviceId;
			this.ReplyExpected = replyExpected;
			this.S = s;
			this.F = f;
			this.MessageType = messageType;
			this.SystemBytes = systemBytes;
		}

		internal unsafe byte[] EncodeTo(byte[] buffer)
		{
			this.EncodeTo(buffer.AsSpan());
			return buffer;
		}

		internal unsafe void EncodeTo(Span<byte> buffer)
		{
			// DeviceId
			BinaryPrimitives.WriteUInt16BigEndian(buffer, this.DeviceId);

			ref var head = ref MemoryMarshal.GetReference(buffer);
			// S, ReplyExpected
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, 2), (byte)(this.S | (this.ReplyExpected ? 0b1000_0000 : 0)));

			// F
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, 3), this.F);

			Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, 4), 0);

			// MessageType
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref head, 5), (byte)this.MessageType);

			// SystemBytes
			BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(6), this.SystemBytes);
		}

		internal static unsafe MessageHeader Decode(in ReadOnlySpan<byte> buffer)
		{
			ref var head = ref MemoryMarshal.GetReference(buffer);
			var s = Unsafe.ReadUnaligned<byte>(ref Unsafe.Add(ref head, 2));
			return new MessageHeader(
				deviceId: BinaryPrimitives.ReadUInt16BigEndian(buffer),
				replyExpected: (s & 0b1000_0000) != 0,
				s: (byte)(s & 0b0111_111),
				f: Unsafe.ReadUnaligned<byte>(ref Unsafe.Add(ref head, 3)),
				messageType: (MessageType)Unsafe.ReadUnaligned<byte>(ref Unsafe.Add(ref head, 5)),
				systemBytes: BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(6))
			);
		}
	}
}