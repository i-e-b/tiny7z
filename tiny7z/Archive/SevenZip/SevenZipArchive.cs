﻿using pdj.tiny7z.Common;
using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;

namespace pdj.tiny7z.Archive
{
    public class SevenZipArchive : Archive
    {
        /// <summary>
        /// 7zip file signature
        /// </summary>
        public static readonly Byte[] kSignature = new Byte[6] { (Byte)'7', (Byte)'z', 0xBC, 0xAF, 0x27, 0x1C };

        /// <summary>
        /// 7zip file archive version
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ArchiveVersion
        {
            [MarshalAs(UnmanagedType.U1)]
            public Byte Major;   // now = 0
            [MarshalAs(UnmanagedType.U1)]
            public Byte Minor;   // now = 2
        };

        /// <summary>
        /// Header part that tells where the actual header starts at the end of the file usually
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct StartHeader
        {
            [MarshalAs(UnmanagedType.U8)]
            public UInt64 NextHeaderOffset;

            [MarshalAs(UnmanagedType.U8)]
            public UInt64 NextHeaderSize;

            [MarshalAs(UnmanagedType.U4)]
            public UInt32 NextHeaderCRC;
        }

        /// <summary>
        /// Signature header of a valid 7zip file
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SignatureHeader
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public Byte[] Signature;

            public ArchiveVersion ArchiveVersion;

            [MarshalAs(UnmanagedType.U4)]
            public UInt32 StartHeaderCRC;

            public StartHeader StartHeader;
        }

        /// <summary>
        /// Header accessor property
        /// </summary>
        SevenZipHeader Header
        {
            get; set;
        }

        /// <summary>
        /// Private variables
        /// </summary>
        SignatureHeader signatureHeader;
        Stream stream;
        FileAccess? fileAccess;

        /// <summary>
        /// Defaut constructor
        /// </summary>
        public SevenZipArchive()
        {
            stream = null;
            fileAccess = null;
            Header = null;
            IsValid = false;
        }

        /// <summary>
        /// Construct a 7zip file with an existing stream
        /// </summary>
        public SevenZipArchive(Stream stream, FileAccess fileAccess)
            : this()
        {
            this.stream = stream;
            this.fileAccess = fileAccess;
            if (fileAccess == FileAccess.Read)
            {
                Trace.TraceInformation("Open 7zip archive for reading.");
                Open();
            }
            else if (fileAccess == FileAccess.Write)
            {
                Trace.TraceInformation("Open 7zip archive for writing.");
                Create();
            }
            else
            {
                throw new ArgumentException("`fileAccess` must be either `Read` or `Write`.");
            }
        }

        /// <summary>
        /// Returns an extractor object to retrieve files from
        /// </summary>
        /// <returns></returns>
        public override IExtractor Extractor()
        {
            return new SevenZipExtractor(stream, Header);
        }

        /// <summary>
        /// Returns a compressor object to compress files into
        /// </summary>
        /// <returns></returns>
        public override ICompressor Compressor()
        {
            return new SevenZipCompressor(stream, Header);
        }

        public void Close()
        {
            this.stream.Close();
        }

        /// <summary>
        /// Dump debug information to console
        /// </summary>
        public void Dump()
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(Header, Newtonsoft.Json.Formatting.Indented);
            Trace.WriteLine(json);
        }

        /// <summary>
        /// Open an existing 7zip file for reading
        /// </summary>
        void Open()
        {
            SignatureHeader sig = stream.ReadStruct<SignatureHeader>();
            if (!sig.Signature.SequenceEqual(kSignature))
            {
                throw new SevenZipException("File is not a valid 7zip file.");
            }
            this.signatureHeader = sig;

            // some debug info

            Trace.TraceInformation("Opening 7zip file:");
            Trace.Indent();

            try
            {
                Trace.TraceInformation($"Version: {sig.ArchiveVersion.Major}.{sig.ArchiveVersion.Minor}");
                Trace.TraceInformation($"StartHeaderCRC: {sig.StartHeaderCRC.ToString("X8")}");
                Trace.TraceInformation($"NextHeaderOffset: {sig.StartHeader.NextHeaderOffset}");
                Trace.TraceInformation($"NextHeaderCRC: {sig.StartHeader.NextHeaderCRC.ToString("X8")}");
                Trace.TraceInformation($"NextHeaderSize: {sig.StartHeader.NextHeaderSize}");
                Trace.TraceInformation($"All headers: " + (sig.StartHeader.NextHeaderSize + (uint)Marshal.SizeOf(sig)) + " bytes");

                {
                    uint crc32 = CRC.Calculate(sig.StartHeader.GetByteArray());
                    if (crc32 != sig.StartHeaderCRC)
                    {
                        throw new SevenZipException("StartHeaderCRC mismatch: " + crc32.ToString("X8"));
                    }
                }

                // buffer header in memory for further processing

                byte[] buffer = new byte[sig.StartHeader.NextHeaderSize];
                stream.Seek((long)sig.StartHeader.NextHeaderOffset, SeekOrigin.Current);
                if (stream.Read(buffer, 0, (int)sig.StartHeader.NextHeaderSize) != (int)sig.StartHeader.NextHeaderSize)
                {
                    throw new SevenZipException("Reached end of file before end of header.");
                }

                {
                    uint crc32 = CRC.Calculate(buffer);
                    if (crc32 != sig.StartHeader.NextHeaderCRC)
                    {
                        throw new SevenZipException("StartHeader.NextHeaderCRC mismatch: " + crc32.ToString("X8"));
                    }
                }

                // initiate header parsing

                Trace.TraceInformation("Parsing 7zip file header");
                Header = new SevenZipHeader(new MemoryStream(buffer));
                Header.Parse();

                // decompress encoded header if found

                if (Header.RawHeader == null && Header.EncodedHeader != null)
                {
                    Trace.TraceInformation("Encoded header detected, decompressing.");
                    Stream newHeaderStream = new MemoryStream();
                    (new SevenZipStreamsExtractor(stream, Header.EncodedHeader)).Extract(0, newHeaderStream);

                    Trace.TraceInformation("Parsing decompressed header.");
                    Trace.TraceInformation($"Header size: {newHeaderStream.Length} bytes.");
                    newHeaderStream.Position = 0;
                    SevenZipHeader
                        newHeader = new SevenZipHeader(newHeaderStream);
                        newHeader.Parse();
                    Header = newHeader;
                }

                IsValid = true;
            }
            finally
            {
                Trace.Unindent();
                Trace.TraceInformation("Done parsing 7zip file header.");
            }
        }

        /// <summary>
        /// Create a new 7zip file for writing
        /// </summary>
        void Create()
        {
            this.signatureHeader = new SignatureHeader()
            {
                Signature = kSignature.ToArray(),
                ArchiveVersion = new ArchiveVersion()
                {
                    Major = 0,
                    Minor = 2,
                },
            };
            stream.Write(this.signatureHeader.GetByteArray(), 0, Marshal.SizeOf(this.signatureHeader));

            this.Header = new SevenZipHeader(null, true);
        }

    }
}
