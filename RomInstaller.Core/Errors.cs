using System;

namespace RomInstaller.Core;

/// <summary>Base for domain errors we may wish to catch specifically.</summary>
public class RomInstallerException : Exception
{
    public RomInstallerException(string message) : base(message) { }
    public RomInstallerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a required file (ROM, emulator exe, config) is missing.</summary>
public class ResourceMissingException : RomInstallerException
{
    public ResourceMissingException(string message) : base(message) { }
}
