using System;

namespace SmarTTY;

public interface IProjectAwareException
{
    Exception WithProjectName(string text);
}
