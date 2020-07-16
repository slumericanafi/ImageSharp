// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;

namespace SixLabors.ImageSharp.IO
{
    /// <summary>
    /// A readonly stream that add a secondary level buffer in addition to native stream
    /// buffered reading to reduce the overhead of small incremental reads.
    /// </summary>
    internal sealed class BufferedReadStream : Stream
    {
        /// <summary>
        /// The length, in bytes, of the underlying buffer.
        /// </summary>
        public const int BufferLength = 8192;

        private const int MaxBufferIndex = BufferLength - 1;

        private readonly byte[] readBuffer;

        private MemoryHandle readBufferHandle;

        private readonly unsafe byte* pinnedReadBuffer;

        // Index within our buffer, not reader position.
        private int readBufferIndex;

        // Matches what the stream position would be without buffering
        private long readerPosition;

        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="BufferedReadStream"/> class.
        /// </summary>
        /// <param name="stream">The input stream.</param>
        public BufferedReadStream(Stream stream)
        {
            Guard.IsTrue(stream.CanRead, nameof(stream), "Stream must be readable.");
            Guard.IsTrue(stream.CanSeek, nameof(stream), "Stream must be seekable.");

            // Ensure all underlying buffers have been flushed before we attempt to read the stream.
            // User streams may have opted to throw from Flush if CanWrite is false
            // (although the abstract Stream does not do so).
            if (stream.CanWrite)
            {
                stream.Flush();
            }

            this.BaseStream = stream;
            this.Position = (int)stream.Position;
            this.Length = stream.Length;

            this.readBuffer = ArrayPool<byte>.Shared.Rent(BufferLength);
            this.readBufferHandle = new Memory<byte>(this.readBuffer).Pin();
            unsafe
            {
                this.pinnedReadBuffer = (byte*)this.readBufferHandle.Pointer;
            }

            // This triggers a full read on first attempt.
            this.readBufferIndex = BufferLength;
        }

        /// <inheritdoc/>
        public override long Length { get; }

        /// <inheritdoc/>
        public override long Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => this.readerPosition;

            [MethodImpl(MethodImplOptions.NoInlining)]
            set
            {
                // Only reset readBufferIndex if we are out of bounds of our working buffer
                // otherwise we should simply move the value by the diff.
                if (this.IsInReadBuffer(value, out long index))
                {
                    this.readBufferIndex = (int)index;
                    this.readerPosition = value;
                }
                else
                {
                    // Base stream seek will throw for us if invalid.
                    this.BaseStream.Seek(value, SeekOrigin.Begin);
                    this.readerPosition = value;
                    this.readBufferIndex = BufferLength;
                }
            }
        }

        /// <inheritdoc/>
        public override bool CanRead { get; } = true;

        /// <inheritdoc/>
        public override bool CanSeek { get; } = true;

        /// <inheritdoc/>
        public override bool CanWrite { get; } = false;

        /// <summary>
        /// Gets the underlying stream.
        /// </summary>
        public Stream BaseStream
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int ReadByte()
        {
            if (this.readerPosition >= this.Length)
            {
                return -1;
            }

            // Our buffer has been read.
            // We need to refill and start again.
            if (this.readBufferIndex > MaxBufferIndex)
            {
                this.FillReadBuffer();
            }

            this.readerPosition++;
            unsafe
            {
                return this.pinnedReadBuffer[this.readBufferIndex++];
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Read(byte[] buffer, int offset, int count)
        {
            // Too big for our buffer. Read directly from the stream.
            if (count > BufferLength)
            {
                return this.ReadToBufferDirectSlow(buffer, offset, count);
            }

            // Too big for remaining buffer but less than entire buffer length
            // Copy to buffer then read from there.
            if (count + this.readBufferIndex > BufferLength)
            {
                return this.ReadToBufferViaCopySlow(buffer, offset, count);
            }

            return this.ReadToBufferViaCopyFast(buffer, offset, count);
        }

        /// <inheritdoc/>
        public override void Flush()
        {
            // Reset the stream position to match reader position.
            if (this.readerPosition != this.BaseStream.Position)
            {
                this.BaseStream.Seek(this.readerPosition, SeekOrigin.Begin);
                this.readerPosition = (int)this.BaseStream.Position;
            }

            // Reset to trigger full read on next attempt.
            this.readBufferIndex = BufferLength;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    this.Position = offset;
                    break;

                case SeekOrigin.Current:
                    this.Position += offset;
                    break;

                case SeekOrigin.End:
                    this.Position = this.Length - offset;
                    break;
            }

            return this.readerPosition;
        }

        /// <inheritdoc/>
        /// <exception cref="NotSupportedException">
        /// This operation is not supported in <see cref="BufferedReadStream"/>.
        /// </exception>
        public override void SetLength(long value)
            => throw new NotSupportedException();

        /// <inheritdoc/>
        /// <exception cref="NotSupportedException">
        /// This operation is not supported in <see cref="BufferedReadStream"/>.
        /// </exception>
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (!this.isDisposed)
            {
                this.isDisposed = true;
                this.readBufferHandle.Dispose();
                ArrayPool<byte>.Shared.Return(this.readBuffer);
                this.Flush();

                base.Dispose(true);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsInReadBuffer(long newPosition, out long index)
        {
            index = newPosition - this.readerPosition + this.readBufferIndex;
            return index > -1 && index < BufferLength;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FillReadBuffer()
        {
            if (this.readerPosition != this.BaseStream.Position)
            {
                this.BaseStream.Seek(this.readerPosition, SeekOrigin.Begin);
            }

            // Read doesn't always guarantee the full returned length so read a byte
            // at a time until we get either our count or hit the end of the stream.
            int n = 0;
            int i;
            do
            {
                i = this.BaseStream.Read(this.readBuffer, n, BufferLength - n);
                n += i;
            }
            while (n < BufferLength && i > 0);

            this.readBufferIndex = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadToBufferViaCopyFast(byte[] buffer, int offset, int count)
        {
            int n = this.GetCopyCount(count);
            this.CopyBytes(buffer, offset, n);

            this.readerPosition += n;
            this.readBufferIndex += n;

            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadToBufferViaCopySlow(byte[] buffer, int offset, int count)
        {
            // Refill our buffer then copy.
            this.FillReadBuffer();

            return this.ReadToBufferViaCopyFast(buffer, offset, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int ReadToBufferDirectSlow(byte[] buffer, int offset, int count)
        {
            // Read to target but don't copy to our read buffer.
            if (this.readerPosition != this.BaseStream.Position)
            {
                this.BaseStream.Seek(this.readerPosition, SeekOrigin.Begin);
            }

            // Read doesn't always guarantee the full returned length so read a byte
            // at a time until we get either our count or hit the end of the stream.
            int n = 0;
            int i;
            do
            {
                i = this.BaseStream.Read(buffer, n + offset, count - n);
                n += i;
            }
            while (n < count && i > 0);

            this.Position += n;

            return n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetCopyCount(int count)
        {
            long n = this.Length - this.readerPosition;
            if (n > count)
            {
                return count;
            }

            if (n < 0)
            {
                return 0;
            }

            return (int)n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void CopyBytes(byte[] buffer, int offset, int count)
        {
            // Same as MemoryStream.
            if (count < 9)
            {
                int byteCount = count;
                int read = this.readBufferIndex;
                byte* pinned = this.pinnedReadBuffer;

                while (--byteCount > -1)
                {
                    buffer[offset + byteCount] = pinned[read + byteCount];
                }
            }
            else
            {
                Buffer.BlockCopy(this.readBuffer, this.readBufferIndex, buffer, offset, count);
            }
        }
    }
}
