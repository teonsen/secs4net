﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Secs4Net
{
	/// <summary>
	///  Stream based HSMS/SECS-II message decoder
	/// </summary>
	internal sealed class StreamDecoder
	{
		private readonly Action<MessageHeader> controlMessageHandler;

		private readonly Action<MessageHeader, SecsMessage> dataMessageHandler;

		/// <summary>
		/// decode pipelines
		/// </summary>
		private readonly Decoder[] decoders;

		private readonly byte[] itemLengthBytes = new byte[4];

		private readonly object lockObject = new object();

		private readonly Stack<List<Item>> stack = new Stack<List<Item>>();

		/// <summary>
		/// Control the range of data decoder
		/// </summary>
		private int decodeIndex;

		private int decoderStep;

		private SecsFormat format;

		private int itemLength;

		private byte lengthBits;

		private uint messageDataLength;

		private MessageHeader messageHeader;

		/// <summary>
		/// previous decoded remained count
		/// </summary>
		private int previousRemainedCount;

		internal StreamDecoder(int streamBufferSize, Action<MessageHeader> controlMessageHandler, Action<MessageHeader, SecsMessage> dataMessageHandler)
		{
			this.Buffer = new byte[streamBufferSize];
			this.BufferOffset = 0;
			this.decodeIndex = 0;
			this.dataMessageHandler = dataMessageHandler;
			this.controlMessageHandler = controlMessageHandler;

			this.decoders = new Decoder[]
			{
				this.DecoderStep0GetTotalMessageLength,
				this.DecoderStep1GetMessageHeader,
				this.DecoderStep2GetItemHeader,
				this.DecoderStep3GetItemLength,
				this.DecoderStep4GetItem,
			};
		}

		/// <summary>
		/// decoder step
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>pipeline decoder index</returns>
		private delegate int Decoder(ref int length, out int need);

		/// <summary>
		/// data buffer
		/// </summary>
		public byte[] Buffer { get; private set; }

		public int BufferCount => this.Buffer.Length - this.BufferOffset;

		/// <summary>
		/// Control the range of data receiver 
		/// </summary>
		public int BufferOffset { get; private set; }

		/// <summary>
		/// Decodes the <see cref="StreamDecoder.Buffer"/>.
		/// </summary>
		/// <param name="length">The data length.</param>
		/// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is less than or equal to zero.</exception>
		/// <returns><see langword="true"/>, if more data is needed to decode a completed message; otherwise, return <see langword="false"/>.</returns>
		public bool Decode(int length)
		{
			if (length <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(length), length, "The date length must be greater than zero.");
			}

			lock (this.lockObject)
			{
				int remainCount = length + this.previousRemainedCount; // total available length = current length + previous remained
				int need;
				int nexStep = this.decoderStep;

				do
				{
					this.decoderStep = nexStep;
					nexStep = this.decoders[this.decoderStep](ref remainCount, out need);
				}
				while (nexStep != this.decoderStep || need == 0);

				Debug.Assert(this.decodeIndex >= this.BufferOffset, "decode index should be ahead of buffer index");

				Debug.Assert(remainCount >= 0, "remain count is only possible greater than or equal to zero");
				Trace.WriteLine($"remain data length: {remainCount}");
				Trace.WriteLineIf(this.messageDataLength > 0, $"need data count: {need}");

				if (remainCount == 0)
				{
					if (need > this.Buffer.Length)
					{
						int newSize = need << 1;
						Trace.WriteLine($"<<buffer resizing>>: current size = {this.Buffer.Length}, new size = {newSize}");

						// increase buffer size
						this.Buffer = new byte[newSize];
					}
					this.BufferOffset = 0;
					this.decodeIndex = 0;
					this.previousRemainedCount = 0;
				}
				else
				{
					this.BufferOffset += length; // move next receive index
					int nextStepReqiredCount = remainCount + need;
					if (nextStepReqiredCount > this.BufferCount)
					{
						if (nextStepReqiredCount > this.Buffer.Length)
						{
							int newSize = (int)(Math.Max(this.messageDataLength >> 1, nextStepReqiredCount) << 1);
							Trace.WriteLine($"<<buffer resizing>>: current size = {this.Buffer.Length}, remained = {remainCount}, new size = {newSize}");

							// out of total buffer size
							// increase buffer size
							byte[] newBuffer = new byte[newSize];
							// keep remained data to new buffer's head
							Array.Copy(this.Buffer, this.BufferOffset - remainCount, newBuffer, 0, remainCount);
							this.Buffer = newBuffer;
						}
						else
						{
							Trace.WriteLine($"<<buffer recyling>>: available = {this.BufferCount}, need = {nextStepReqiredCount}, remained = {remainCount}");

							// move remained data to buffer's head
							Array.Copy(this.Buffer, this.BufferOffset - remainCount, this.Buffer, 0, remainCount);
						}
						this.BufferOffset = remainCount;
						this.decodeIndex = 0;
					}
					this.previousRemainedCount = remainCount;
				}

				return this.messageDataLength > 0;
			}
		}

		public void Reset()
		{
			lock (this.lockObject)
			{
				this.stack.Clear();
				this.decoderStep = 0;
				this.decodeIndex = 0;
				this.BufferOffset = 0;
				this.messageDataLength = 0;
				this.previousRemainedCount = 0;
			}
		}

		private static Item BufferedDecodeItem(byte[] bytes, ref int index)
		{
			byte formatCodeValue = bytes[index];
			var format = (SecsFormat)(formatCodeValue & 0b_111111_00);
			byte lengthBits = (byte)(formatCodeValue & 0b_000000_11);
			index++;

			byte[] itemLengthBytes = new byte[4];
			Array.Copy(bytes, index, itemLengthBytes, 0, lengthBits);
			Array.Reverse(itemLengthBytes, 0, lengthBits);
			int dataLength = BitConverter.ToInt32(itemLengthBytes, 0); // max to 3 byte dataLength
			index += lengthBits;

			if (format == SecsFormat.List)
			{
				if (dataLength == 0)
				{
					return Item.L();
				}

				var list = new List<Item>(dataLength);
				for (int i = 0; i < dataLength; i++)
				{
					list.Add(StreamDecoder.BufferedDecodeItem(bytes, ref index));
				}

				return Item.L(list);
			}
			var item = Item.BytesDecode(format, bytes, index, dataLength);
			index += dataLength;
			return item;
		}

		private static bool CheckAvailable(int length, int required, out int need)
		{
			need = required - length;
			if (need > 0)
			{
				return false;
			}
			need = 0;
			return true;
		}

		/// <summary>
		/// Decoder step 0: get total message length 4 bytes 
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>The decoder step to execute next.</returns>
		private int DecoderStep0GetTotalMessageLength(ref int length, out int need)
		{
			if (!StreamDecoder.CheckAvailable(length, 4, out need))
			{
				return 0;
			}

			Array.Reverse(this.Buffer, this.decodeIndex, 4);
			this.messageDataLength = BitConverter.ToUInt32(this.Buffer, this.decodeIndex);
			Trace.WriteLine($"Get Message Length: {this.messageDataLength}");
			this.decodeIndex += 4;
			length -= 4;
			return this.DecoderStep1GetMessageHeader(ref length, out need);
		}

		/// <summary>
		/// Decoder step 1: get message header 10 bytes 
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>The decoder step to execute next.</returns>
		private int DecoderStep1GetMessageHeader(ref int length, out int need)
		{
			if (!StreamDecoder.CheckAvailable(length, 10, out need))
			{
				return 1;
			}

			this.messageHeader = MessageHeader.Decode(new ReadOnlySpan<byte>(this.Buffer, this.decodeIndex, 10));
			this.decodeIndex += 10;
			this.messageDataLength -= 10;
			length -= 10;
			if (this.messageDataLength == 0)
			{
				if (this.messageHeader.MessageType == MessageType.DataMessage)
				{
					this.dataMessageHandler(this.messageHeader, new SecsMessage(this.messageHeader.S, this.messageHeader.F, string.Empty, replyExpected: this.messageHeader.ReplyExpected));
				}
				else
				{
					this.controlMessageHandler(this.messageHeader);
				}

				return 0;
			}

			if (length >= this.messageDataLength)
			{
				Trace.WriteLine("Get Complete Data Message with total data");
				this.dataMessageHandler(this.messageHeader, new SecsMessage(this.messageHeader.S, this.messageHeader.F, string.Empty, StreamDecoder.BufferedDecodeItem(this.Buffer, ref this.decodeIndex), this.messageHeader.ReplyExpected));
				length -= (int)this.messageDataLength;
				this.messageDataLength = 0;
				return 0; //completeWith message received
			}
			return this.DecoderStep2GetItemHeader(ref length, out need);
		}

		/// <summary>
		/// Decoder step 2: get _format + lengthBits(2bit) 1 byte
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>The decoder step to execute next.</returns>
		private int DecoderStep2GetItemHeader(ref int length, out int need)
		{
			if (!StreamDecoder.CheckAvailable(length, 1, out need))
			{
				return 2;
			}

			this.format = (SecsFormat)(this.Buffer[this.decodeIndex] & 0xFC);
			this.lengthBits = (byte)(this.Buffer[this.decodeIndex] & 3);
			this.decodeIndex++;
			this.messageDataLength--;
			length--;
			return this.DecoderStep3GetItemLength(ref length, out need);
		}

		/// <summary>
		/// Decoder step 3: get _itemLength _lengthBits bytes, at most 3 byte
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>The decoder step to execute next.</returns>
		private int DecoderStep3GetItemLength(ref int length, out int need)
		{
			if (!StreamDecoder.CheckAvailable(length, this.lengthBits, out need))
			{
				return 3;
			}

			Array.Copy(this.Buffer, this.decodeIndex, this.itemLengthBytes, 0, this.lengthBits);
			Array.Reverse(this.itemLengthBytes, 0, this.lengthBits);

			this.itemLength = BitConverter.ToInt32(this.itemLengthBytes, 0);
			Array.Clear(this.itemLengthBytes, 0, 4);
			Trace.WriteLineIf(this.format != SecsFormat.List, $"Get format: {this.format}, length: {this.itemLength}");

			this.decodeIndex += this.lengthBits;
			this.messageDataLength -= this.lengthBits;
			length -= this.lengthBits;
			return this.DecoderStep4GetItem(ref length, out need);
		}

		/// <summary>
		/// Decoder step 4: get item value
		/// </summary>
		/// <param name="length"></param>
		/// <param name="need"></param>
		/// <returns>The decoder step to execute next.</returns>
		private int DecoderStep4GetItem(ref int length, out int need)
		{
			need = 0;
			Item item;
			if (this.format == SecsFormat.List)
			{
				if (this.itemLength == 0)
				{
					item = Item.L();
				}
				else
				{
					this.stack.Push(new List<Item>(this.itemLength));
					return this.DecoderStep2GetItemHeader(ref length, out need);
				}
			}
			else
			{
				if (!StreamDecoder.CheckAvailable(length, this.itemLength, out need))
				{
					return 4;
				}

				item = Item.BytesDecode(this.format, this.Buffer, this.decodeIndex, this.itemLength);
				Trace.WriteLine($"Complete Item: {this.format}");

				this.decodeIndex += this.itemLength;
				this.messageDataLength -= (uint)this.itemLength;
				length -= this.itemLength;
			}

			if (this.stack.Count == 0)
			{
				Trace.WriteLine("Get Complete Data Message by stream decoded");
				this.dataMessageHandler(this.messageHeader, new SecsMessage(this.messageHeader.S, this.messageHeader.F, string.Empty, item, this.messageHeader.ReplyExpected));
				return 0;
			}

			var list = this.stack.Peek();
			list.Add(item);
			while (list.Count == list.Capacity)
			{
				item = Item.L(this.stack.Pop());
				Trace.WriteLine($"Complete List: {item.Count}");
				if (this.stack.Count > 0)
				{
					list = this.stack.Peek();
					list.Add(item);
				}
				else
				{
					Trace.WriteLine("Get Complete Data Message by stream decoded");
					this.dataMessageHandler(this.messageHeader, new SecsMessage(this.messageHeader.S, this.messageHeader.F, string.Empty, item, this.messageHeader.ReplyExpected));
					return 0;
				}
			}

			return this.DecoderStep2GetItemHeader(ref length, out need);
		}
	}
}