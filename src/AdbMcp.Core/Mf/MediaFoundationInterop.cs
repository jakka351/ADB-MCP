using System;
using System.Runtime.InteropServices;

namespace AdbMcp.Mf
{
    /// <summary>
    /// Minimal Media Foundation COM interop for driving the in-box H.264 decoder MFT.
    /// Interface methods are declared in exact vtable order (unused slots are placeholders)
    /// because COM dispatch is positional. Only the methods actually called carry real
    /// signatures. This is Windows-only and unavoidably low-level; every entry point here
    /// is exercised behind defensive try/catch in MediaFoundationH264Decoder so a failure
    /// disables decoding rather than crashing the server.
    /// </summary>
    internal static class Mf
    {
        public const uint MF_VERSION = 0x00020070;
        public const uint MFSTARTUP_LITE = 1;

        public const uint MFT_MESSAGE_COMMAND_FLUSH = 0x00000000;
        public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
        public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000001;
        public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000002;

        public const int S_OK = 0;
        public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
        public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
        public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

        public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x00000100;

        // CLSID_CMSH264DecoderMFT
        public static readonly Guid CLSID_H264Decoder = new Guid("62CE7E72-4C71-4d20-B15D-452831A87D9D");

        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");

        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

        public const uint MFVideoInterlace_Progressive = 2;

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(uint version, uint dwFlags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

        public static Guid FrameSizeKey => MF_MT_FRAME_SIZE;

        public static ulong PackSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_STREAM_INFO
    {
        public uint dwFlags;
        public uint cbSize;
        public uint cbAlignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MFT_OUTPUT_DATA_BUFFER
    {
        public uint dwStreamID;
        [MarshalAs(UnmanagedType.IUnknown)] public object pSample;
        public uint dwStatus;
        [MarshalAs(UnmanagedType.IUnknown)] public object pEvents;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
        [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
    }

    // IMFMediaType : IMFAttributes. Slots 1..22 declared; only the used ones have real signatures.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    internal interface IMFMediaType
    {
        [PreserveSig] int vt01_GetItem();
        [PreserveSig] int vt02_GetItemType();
        [PreserveSig] int vt03_CompareItem();
        [PreserveSig] int vt04_Compare();
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);          // 5
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);         // 6
        [PreserveSig] int vt07_GetDouble();
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);            // 8
        [PreserveSig] int vt09_GetStringLength();
        [PreserveSig] int vt10_GetString();
        [PreserveSig] int vt11_GetAllocatedString();
        [PreserveSig] int vt12_GetBlobSize();
        [PreserveSig] int vt13_GetBlob();
        [PreserveSig] int vt14_GetAllocatedBlob();
        [PreserveSig] int vt15_GetUnknown();
        [PreserveSig] int vt16_SetItem();
        [PreserveSig] int vt17_DeleteItem();
        [PreserveSig] int vt18_DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);              // 19
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);            // 20
        [PreserveSig] int vt21_SetDouble();
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);       // 22
    }

    // IMFSample : IMFAttributes(30) + sample methods. Slots 1..40 declared.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    internal interface IMFSample
    {
        [PreserveSig] int vt01(); [PreserveSig] int vt02(); [PreserveSig] int vt03(); [PreserveSig] int vt04();
        [PreserveSig] int vt05(); [PreserveSig] int vt06(); [PreserveSig] int vt07(); [PreserveSig] int vt08();
        [PreserveSig] int vt09(); [PreserveSig] int vt10(); [PreserveSig] int vt11(); [PreserveSig] int vt12();
        [PreserveSig] int vt13(); [PreserveSig] int vt14(); [PreserveSig] int vt15(); [PreserveSig] int vt16();
        [PreserveSig] int vt17(); [PreserveSig] int vt18(); [PreserveSig] int vt19(); [PreserveSig] int vt20();
        [PreserveSig] int vt21(); [PreserveSig] int vt22(); [PreserveSig] int vt23(); [PreserveSig] int vt24();
        [PreserveSig] int vt25(); [PreserveSig] int vt26(); [PreserveSig] int vt27(); [PreserveSig] int vt28();
        [PreserveSig] int vt29(); [PreserveSig] int vt30();
        [PreserveSig] int vt31_GetSampleFlags();
        [PreserveSig] int vt32_SetSampleFlags();
        [PreserveSig] int vt33_GetSampleTime();
        [PreserveSig] int SetSampleTime(long hnsSampleTime);                     // 34
        [PreserveSig] int vt35_GetSampleDuration();
        [PreserveSig] int vt36_SetSampleDuration();
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);               // 37
        [PreserveSig] int vt38_GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer); // 39
        [PreserveSig] int AddBuffer(IMFMediaBuffer pBuffer);                     // 40
    }

    // IMFTransform : IUnknown. All 23 slots declared.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    internal interface IMFTransform
    {
        [PreserveSig] int vt01_GetStreamLimits();
        [PreserveSig] int vt02_GetStreamCount();
        [PreserveSig] int vt03_GetStreamIDs();
        [PreserveSig] int vt04_GetInputStreamInfo();
        [PreserveSig] int GetOutputStreamInfo(uint dwOutputStreamID, out MFT_OUTPUT_STREAM_INFO pStreamInfo); // 5
        [PreserveSig] int vt06_GetAttributes();
        [PreserveSig] int vt07_GetInputStreamAttributes();
        [PreserveSig] int vt08_GetOutputStreamAttributes();
        [PreserveSig] int vt09_DeleteInputStream();
        [PreserveSig] int vt10_AddInputStreams();
        [PreserveSig] int vt11_GetInputAvailableType();
        [PreserveSig] int GetOutputAvailableType(uint dwOutputStreamID, uint dwTypeIndex, out IMFMediaType ppType); // 12
        [PreserveSig] int SetInputType(uint dwInputStreamID, IMFMediaType pType, uint dwFlags);   // 13
        [PreserveSig] int SetOutputType(uint dwOutputStreamID, IMFMediaType pType, uint dwFlags); // 14
        [PreserveSig] int vt15_GetInputCurrentType();
        [PreserveSig] int GetOutputCurrentType(uint dwOutputStreamID, out IMFMediaType ppType);   // 16
        [PreserveSig] int vt17_GetInputStatus();
        [PreserveSig] int vt18_GetOutputStatus();
        [PreserveSig] int vt19_SetOutputBounds();
        [PreserveSig] int vt20_ProcessEvent();
        [PreserveSig] int ProcessMessage(uint eMessage, IntPtr ulParam);         // 21
        [PreserveSig] int ProcessInput(uint dwInputStreamID, IMFSample pSample, uint dwFlags);    // 22
        [PreserveSig] int ProcessOutput(uint dwFlags, uint cOutputBufferCount,
            [In, Out] MFT_OUTPUT_DATA_BUFFER[] pOutputSamples, out uint pdwStatus);               // 23
    }
}
