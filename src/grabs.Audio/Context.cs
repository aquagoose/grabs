﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using grabs.Audio.Internal;
using Buffer = grabs.Audio.Internal.Buffer;

namespace grabs.Audio;

public sealed class Context
{
    public readonly uint SampleRate;

    private ulong _numBuffers;
    private Buffer[] _buffers;

    private ulong _numSources;
    private Source[] _sources;

    public float MasterVolume;
    
    public Context(uint sampleRate)
    {
        SampleRate = sampleRate;

        _buffers = new Buffer[1];
        _sources = new Source[1];

        MasterVolume = 1.0f;
    }

    public AudioBuffer CreateBuffer<T>(in BufferDescription description, T[] data) where T : unmanaged
        => CreateBuffer(description, new ReadOnlySpan<T>(data));

    public unsafe AudioBuffer CreateBuffer<T>(in BufferDescription description, in ReadOnlySpan<T> data) where T : unmanaged
    {
        AudioFormat format = description.Format;
        
        if (_numBuffers + 1 >= (ulong) _buffers.Length)
            Array.Resize(ref _buffers, _buffers.Length << 1);

        uint dataLength = (uint) (data.Length * sizeof(T));
        byte[] byteData = new byte[dataLength];
        
        fixed (void* pByteData = byteData)
        fixed (void* pData = data)
            Unsafe.CopyBlock(pByteData, pData, dataLength);

        ulong bufferIndex = _numBuffers++;

        ulong channels = format.Channels switch
        {
            Channels.Mono => 1,
            Channels.Stereo => 2,
            _ => throw new ArgumentOutOfRangeException()
        };
        
        _buffers[bufferIndex] = new Buffer
        {
            Data = byteData,
            Format = format,
            PcmType = description.PcmType,
            
            LengthInSamples = (ulong) (byteData.Length / (format.DataType.Bytes() * (int) channels)),

            ByteAlign = (ulong) format.DataType.Bytes(),
            StereoAlign = (ulong) format.DataType.Bytes() * (channels - 1),
            Channels = channels,
            
            SpeedCorrection = format.SampleRate / (float) SampleRate
        };

        return new AudioBuffer(this, bufferIndex);
    }

    public AudioSource CreateSource()
    {
        if (_numSources + 1 >= (ulong) _sources.Length)
            Array.Resize(ref _sources, _sources.Length << 1);

        ulong sourceIndex = _numSources++;
        _sources[sourceIndex] = new Source()
        {
            QueuedBuffers = new Queue<ulong>(),
            Playing = false,
            Speed = 1,
            Volume = 1,
            Looping = false,
            Position = 0,
            FinePosition = 0
        };

        return new AudioSource(this, sourceIndex);
    }

    internal void SubmitBufferToSource(ulong bufferId, ulong sourceId)
    {
        _sources[sourceId].QueuedBuffers.Enqueue(bufferId);
    }

    internal void SourcePlay(ulong sourceId)
    {
        ref Source source = ref _sources[sourceId];

        source.Position = 0;
        source.FinePosition = 0;
        source.LastPosition = 0;
        source.LerpPosition = 0;
        source.Playing = true;
    }

    internal void SourceSetSpeed(ulong sourceId, double speed)
    {
        _sources[sourceId].Speed = speed;
    }

    internal void SourceSetVolume(ulong sourceId, float volume)
    {
        _sources[sourceId].Volume = volume;
    }

    internal void SourceSetLooping(ulong sourceId, bool looping)
    {
        _sources[sourceId].Looping = looping;
    }

    internal void MixIntoBufferStereoF32(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i += 2)
        {
            buffer[i + 0] = 0;
            buffer[i + 1] = 0;
            
            for (ulong s = 0; s < _numSources; s++)
            {
                ref Source source = ref _sources[s];
                
                if (!source.Playing)
                    continue;

                ulong bufferId = source.QueuedBuffers.Peek();
                ref Buffer buf = ref _buffers[bufferId];

                ref AudioFormat format = ref buf.Format;

                ulong bytePosition = source.Position * (buf.ByteAlign + buf.StereoAlign);

                float sampleL = GetSample(buf.Data, bytePosition, format.DataType);
                float sampleR = GetSample(buf.Data, bytePosition + buf.StereoAlign, format.DataType);

                ulong lastPosition = source.LerpPosition * buf.ByteAlign * buf.Channels;
                float lastSampleL = GetSample(buf.Data, lastPosition, format.DataType);
                float lastSampleR = GetSample(buf.Data, lastPosition + buf.StereoAlign, format.DataType);

                sampleL = float.Lerp(lastSampleL, sampleL, (float) source.FinePosition);
                sampleR = float.Lerp(lastSampleR, sampleR, (float) source.FinePosition);

                buffer[i + 0] += float.Clamp(sampleL * source.Volume, -1.0f, 1.0f);
                buffer[i + 1] += float.Clamp(sampleR * source.Volume, -1.0f, 1.0f);

                source.FinePosition += buf.SpeedCorrection * source.Speed;

                ulong intFine = (ulong) source.FinePosition;
                source.Position += intFine;
                source.FinePosition -= intFine;

                if (source.Position != source.LastPosition)
                {
                    source.LerpPosition = source.LastPosition;
                    source.LastPosition = source.Position;
                }

                if (source.Position >= buf.LengthInSamples)
                {
                    // To ensure that looped samples play perfectly
                    if (source.Looping)
                    {
                        // Not actually yet implemented
                        /*source.Position -= buf.LengthInSamples;
                        source.LastPosition -= buf.LengthInSamples;
                        source.LerpPosition -= buf.LengthInSamples;*/
                        source.Position = 0;
                        source.LastPosition = 0;
                        source.FinePosition = 0;
                    }
                    else
                        source.Playing = false;
                }
            }

            buffer[i + 0] = float.Clamp(buffer[i + 0] * MasterVolume, -1.0f, 1.0f);
            buffer[i + 1] = float.Clamp(buffer[i + 1] * MasterVolume, -1.0f, 1.0f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe float GetSample(byte[] data, ulong index, DataType type)
    {
        switch (type)
        {
            case DataType.U8:
                throw new NotImplementedException();
            case DataType.I16:
                return (short) (data[index + 0] | data[index + 1] << 8) / (float) short.MaxValue;
            case DataType.I32:
                throw new NotImplementedException();
            case DataType.F32:
            {
                int result = data[index + 0] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24);
                return *(float*) &result;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
}