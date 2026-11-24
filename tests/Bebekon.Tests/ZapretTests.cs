using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public sealed class ZapretTests
{
    private const string Bin = @"C:\ProgramData\BebekonVPN\zapret\bin";
    private const string Lists = @"C:\ProgramData\BebekonVPN\zapret\lists";
    private static string Batch(string args) => "@echo off\r\nchcp 65001 > nul\r\ncd /d \"%~dp0\"\r\ncall service.bat status_zapret\r\nset \"BIN=%~dp0bin\\\"\r\nset \"LISTS=%~dp0lists\\\"\r\ncd /d %BIN%\r\n\r\n" +
        "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^\r\n" + args + "\r\n";

    [Fact]
    public void ParsesFlowsealProfileAndKeepsFilesInsideTheCopy()
    {
        var profile = Zapret.Parse("general (ALT9).bat", Batch(
            "--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --ip-id=zero --dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --new ^\r\n" +
            "--filter-udp=443 --dpi-desync=fake --dpi-desync-fake-quic=\"%BIN%quic_initial_www_google_com.bin\""), Bin, Lists);
        Assert.Equal("--wf-tcp=80,443,12", profile.Arguments[0]);
        Assert.Contains(@"--hostlist=" + Lists + @"\list-google.txt", profile.Arguments);
        Assert.Contains(@"--dpi-desync-fake-quic=" + Bin + @"\quic_initial_www_google_com.bin", profile.Arguments);
        Assert.Contains("--new", profile.Arguments);
        Assert.True(profile.NeedsTcpTimestamps);
        Assert.Contains("www.google.com", profile.Summary); Assert.Contains("×4", profile.Summary);
    }

    [Theory]
    [InlineData("--hostlist=\"C:\\Windows\\System32\\drivers\\etc\\hosts\"")]  // file outside the copy
    [InlineData("--hostlist=\"%LISTS%..\\..\\secret.txt\"")]                   // escapes the copy
    [InlineData("--dpi-desync=fake & calc.exe")]                                // chained command
    [InlineData("--dpi-desync=%EVIL%")]                                         // unknown variable
    [InlineData("calc.exe")]                                                     // not an option
    [InlineData("--debug=@C:/Windows/System32/evil.dll")]                       // SYSTEM-written log file
    [InlineData("--hostlist=C:/Windows/win.ini")]                               // forward-slash path outside
    [InlineData("--hostlist=@args.txt")]                                        // argument file
    public void RefusesAnythingButWinwsOptionsOnItsOwnFiles(string args)
        => Assert.Throws<UserError>(() => Zapret.Parse("general.bat", Batch(args), Bin, Lists));

    [Fact]
    public void RefusesUnknownProfileNamesAndBatchesWithoutWinws()
    {
        Assert.Throws<UserError>(() => Zapret.Parse("evil.bat", Batch("--dpi-desync=fake"), Bin, Lists));
        Assert.Throws<UserError>(() => Zapret.Parse("general.bat", "@echo off\r\necho hi\r\n", Bin, Lists));
    }

    [Fact]
    public void DescribesStrategiesInPlainWords()
    {
        Assert.Equal("разрезание с подменой начала (seqovl)", Zapret.Describe(["--filter-tcp=443", "--hostlist=x\\list-google.txt", "--dpi-desync=multisplit", "--dpi-desync-split-seqovl=681"]));
        Assert.Contains("подделки с неверным номером TCP", Zapret.Describe(["--filter-tcp=443", "--dpi-desync=fake", "--dpi-desync-fooling=badseq"]));
    }

    [Fact]
    public void UserListsAreValidatedBeforeTheHelperWritesThem()
    {
        Assert.Equal(["# mine", "youtube.com", "^exact.example"], Zapret.ValidateList("list-general-user.txt", ["# mine", "youtube.com", "^exact.example"]));
        Assert.Equal(["203.0.113.0/24"], Zapret.ValidateList("ipset-exclude-user.txt", ["203.0.113.0/24"]));
        Assert.Throws<UserError>(() => Zapret.ValidateList("ipset-exclude-user.txt", ["not-an-ip"]));
        Assert.Throws<UserError>(() => Zapret.ValidateList("general.bat", ["x"]));
        Assert.Throws<UserError>(() => Zapret.ValidateList("list-general-user.txt", ["bad host/"]));
    }
}
