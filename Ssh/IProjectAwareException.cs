using System;

namespace SmarTTY.Ssh;

public interface IProjectAwareException
{
    Exception WithProjectName(string text);
}
