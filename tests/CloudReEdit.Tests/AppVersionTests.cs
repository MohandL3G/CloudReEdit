using System.Text.RegularExpressions;
using CloudReEdit.Services;
using Xunit;

namespace CloudReEdit.Tests;

public class AppVersionTests
{
    [Fact]
    public void Display_ReturnsASemverLikeStringWithoutBuildMetadata()
    {
        var display = AppVersion.Display();

        Assert.False(string.IsNullOrWhiteSpace(display));
        Assert.DoesNotContain("+", display);
        Assert.Matches(new Regex(@"^\d+(\.\d+)*$"), display);
    }
}
