// This file is modified from the original version, which is licensed under the Apache License, Version 2.0.
// Modifications made by MIR.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SnippingToolOcrCore
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Img
    {
        public int t;
        public int col;
        public int row;
        public int _unk;
        public long step;
        public IntPtr data_ptr;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BoundingBox
    {
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public float x3;
        public float y3;
        public float x4;
        public float y4;
    }
    internal partial class NativeMethods
    {
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrInitOptions(out long ctx);
        
        [LibraryImport("oneocr", StringMarshalling = StringMarshalling.Utf8)]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrPipeline(string modelPath, string key, long ctx, out long pipeline);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long CreateOcrProcessOptions(out long opt);
        
        // GetImageAngle

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLine(long instance, long index, out long line);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineBoundingBox(long line, out IntPtr boundingBoxPtr);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineContent(long line, out IntPtr content);

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineCount(long instance, out long count);

        // GetOcrLineStyle

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrLineWordCount(long instance, out long count);

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWord(long instance, long index, out long line);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWordBoundingBox(long line, out IntPtr boundingBoxPtr);
        
        // GetOcrWordConfidence

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long GetOcrWordContent(long line, out IntPtr content);

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrInitOptionsSetUseModelDelayLoad(long ctx, byte flag);
        
        // OcrProcessOptionsGetMaxRecognitionLineCount
        // OcrProcessOptionsGetResizeResolution

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long OcrProcessOptionsSetMaxRecognitionLineCount(long opt, long count);

        // OcrProcessOptionsSetResizeResolution

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrInitOptions(long ctx);

        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrPipeline(long pipeline);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrProcessOptions(long opt);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long ReleaseOcrResult(long instance);
        
        [LibraryImport("oneocr")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        public static partial long RunOcrPipeline(long pipeline, ref Img img, long opt, out long instance);
    }
}
