// This file is modified from the original version, which is licensed under the Apache License, Version 2.0.
// Modifications made by MIR.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace SnippingToolOcrCore;

public class Ocr : IDisposable
{
    private long Context { get; set; }
    private long Pipeline { get; set; }
    private long ProcessOptions { get; set; }
    private ILogger? _logger { get; set; }
    private void Initialize()
    {
        // Initialize context
        var res = NativeMethods.CreateOcrInitOptions(out var ctx);
        if (res != 0)
        {
            throw new InvalidOperationException("Failed to create OCR init options.");
        }
        Context = ctx;

        // Disable model delay load
        res = NativeMethods.OcrInitOptionsSetUseModelDelayLoad(ctx, 0);
        if (res != 0)
        {
            throw new InvalidOperationException("Failed to set model delay load.");
        }
    }

    public Ocr(ILogger? logger = null)
    {
        _logger = logger;
        try
        {
            Initialize();
        }
        catch (DllNotFoundException)
        {
            throw new DllNotFoundException("Can not find oneocr.dll, onnxruntime.dll and oneocr.onemodel");
        }
        
    }

    // The key is for the AI model, if key is not right, CreateOcrPipeline will
    // return 6 with error message: Crypto.cpp:78 Check failed: meta->magic_number
    // == MAGIC_NUMBER (0 vs. 1) Unable to uncompress. Source data mismatch.
    private const string Key = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
    private const string ModelPath = "oneocr.onemodel";

    public void CreatePipelineAndProcessOptions(long maxRecognitionLineCount = 100)
    {
        // Create OCR pipeline
        var res = NativeMethods.CreateOcrPipeline(ModelPath, Key, Context, out var pipeline);
        if (res != 0)
        {
            throw new InvalidOperationException($"Failed to create OCR pipeline. Error code: {res}");
        }
        Pipeline = pipeline;
        _logger?.ZLogDebug($"OCR model loaded");
        
        // Set process options
        res = NativeMethods.CreateOcrProcessOptions(out var opt);
        if (res != 0)
        {
            throw new InvalidOperationException("Failed to create OCR process options.");
        }
        ProcessOptions = opt;
        
        if (maxRecognitionLineCount != 100 && (maxRecognitionLineCount is >= 0 and <= 1000))
        {
            res = NativeMethods.OcrProcessOptionsSetMaxRecognitionLineCount(ProcessOptions, maxRecognitionLineCount);
            if (res != 0)
            {
                _logger?.ZLogError($"Failed to set max recognition line count.");
            }
        }
    }

    public void ReleasePipelineAndProcessOptions()
    {
        if (ProcessOptions != 0) _ = NativeMethods.ReleaseOcrProcessOptions(ProcessOptions);
        if (Pipeline != 0) _ = NativeMethods.ReleaseOcrPipeline(Pipeline);
    }
    
    public Line[]? RunOcr(Img img)
    {
        if (ProcessOptions is 0 || Pipeline is 0)
        {
            throw new InvalidOperationException("Please call CreatePipelineAndProcessOptions() first.");
        }
        
        // Run OCR pipeline
        var res = NativeMethods.RunOcrPipeline(Pipeline, ref img, ProcessOptions, out var instance);
        if (res != 0)
        {
            _logger?.ZLogError($"Failed to run OCR pipeline. Error code: {res}");
            return null;
        }
        _logger?.ZLogDebug($"Running ocr pipeline");

        // Get the number of recognized lines
        res = NativeMethods.GetOcrLineCount(instance, out var lineCount);
        if (res != 0)
        {
            _logger?.ZLogError($"Failed to get OCR line count.");
            return null;
        }
        _logger?.ZLogDebug($"Recognize {lineCount} lines");

        List<Line> lines = [];
        // Get the content of each line
        for (var i = 0; i < lineCount; i++)
        {
            res = NativeMethods.GetOcrLine(instance, i, out var line);
            if (res != 0 || line == 0)
            {
                continue;
            }

            res = NativeMethods.GetOcrLineContent(line, out var lineContentPtr);
            if (res != 0)
            {
                continue;
            }

            var lineContent = Marshal.PtrToStringUTF8(lineContentPtr);

            // Get the pointer to the bounding box
            res = NativeMethods.GetOcrLineBoundingBox(line, out var boundingBoxPtr);
            if (res != 0)
            {
                _logger?.ZLogError($"Failed to get bounding box.");
                continue;
            }
            
            // Map the pointer to the structure
            var boundingBox = Marshal.PtrToStructure<BoundingBox>(boundingBoxPtr);

            var data = new Line
            {
                Text = lineContent,
                X1 = boundingBox.x1,
                Y1 = boundingBox.y1,
                X2 = boundingBox.x2,
                Y2 = boundingBox.y2,
                X3 = boundingBox.x3,
                Y3 = boundingBox.y3,
                X4 = boundingBox.x4,
                Y4 = boundingBox.y4
            };

            res = NativeMethods.GetOcrLineWordCount(line, out var wordCount);
            if (res != 0)
            {
                _logger?.ZLogError($"Failed to get OCR word count.");
                return null;
            }

            List<Word> words = [];
            for (var j = 0; j < wordCount; j++)
            {
                res = NativeMethods.GetOcrWord(line, j, out var word);
                if (res != 0 || word == 0)
                {
                    continue;
                }

                res = NativeMethods.GetOcrWordContent(word, out var wordContentPtr);
                if (res != 0)
                {
                    continue;
                }

                var wordContent = Marshal.PtrToStringUTF8(wordContentPtr);

                // Get the pointer to the bounding box
                res = NativeMethods.GetOcrWordBoundingBox(word, out var wordBoundingBoxPtr);
                if (res != 0)
                {
                    _logger?.ZLogError($"Failed to get bounding box.");
                    continue;
                }
                
                // Map the pointer to the structure
                var wordBoundingBox = Marshal.PtrToStructure<BoundingBox>(wordBoundingBoxPtr);
                var w = new Word
                {
                    Text = wordContent,
                    X1 = wordBoundingBox.x1,
                    Y1 = wordBoundingBox.y1,
                    X2 = wordBoundingBox.x2,
                    Y2 = wordBoundingBox.y2,
                    X3 = wordBoundingBox.x3,
                    Y3 = wordBoundingBox.y3,
                    X4 = wordBoundingBox.x4,
                    Y4 = wordBoundingBox.y4
                };
                words.Add(w);
            }

            data.Words = words.ToArray();
            lines.Add(data);
        }

        // 1
        _ = NativeMethods.ReleaseOcrResult(instance);

        return lines.ToArray();
    }
    
    
    private bool disposedValue;
    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                ReleasePipelineAndProcessOptions();
                _ = NativeMethods.ReleaseOcrInitOptions(Context);
            }

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
            disposedValue = true;
        }
    }
    
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}


