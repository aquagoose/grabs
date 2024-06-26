﻿using System;

namespace grabs.Graphics;

public abstract class Buffer : IDisposable
{
    public readonly BufferDescription Description;

    protected Buffer(BufferDescription description)
    {
        Description = description;
    }

    public abstract void Dispose();
}