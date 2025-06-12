# This is my fork of bstrings - I'm using AI (I'm not writing or extensively manually checking the code) to make the following changes:

1. Faster - apparently this uses the gpu where possible and SIMD methodologies to speed it up (see benchmarks)
2. User should be able to provide multiple --lr (--lr email, url, etc..) or an 'all' (--lr all)
3. Change logging slightly to show a progress bar instead, this makes more sense as we now do chunks concurrently.
4. Works in net9.0, have not tested any other.
5. **NEW**: Automated builds and releases via GitHub Actions - every commit to master creates a new release!
6. **NEW**: Enhanced output control - automatically suppresses console output when using `-q` with `-o` for maximum performance on large files!

> 📦 **Ready-to-use builds**: Check the [Releases page](../../releases/latest) for the latest Windows x64 executable!

## Performance Improvements 🚀

This fork introduces **significant performance improvements** through concurrent regex pattern processing:

### New Multi-Pattern Features

- **Comma-separated patterns**: `--lr "guid,email,cc"` - Process multiple patterns concurrently
- **All patterns**: `--lr all` - Process all 27 built-in patterns at once
- **Backward compatible**: Single patterns still work as before

### Performance Comparison Results

Based on testing with a 2.54 MB test file:

| Test Scenario                  | Original Version | Modified Version | Performance Gain               |
| ------------------------------ | ---------------- | ---------------- | ------------------------------ |
| **Single Pattern (guid)**      | ~1.76 seconds    | ~1.35 seconds    | **23% faster**                 |
| **3 Patterns (guid,email,cc)** | ~5.28 seconds\*  | ~1.39 seconds    | **280% faster (3.8x speedup)** |
| **All Patterns (27 patterns)** | ~47.5 seconds\*  | ~5.66 seconds    | **740% faster (8.4x speedup)** |

\*_Sequential execution time (multiple separate commands)_

### Real-World Impact

**Before (Original)**:

```bash
# Multiple commands required for multiple patterns
bstrings.exe -f file.txt --lr guid -q     # 1.76s
bstrings.exe -f file.txt --lr email -q    # 1.76s
bstrings.exe -f file.txt --lr cc -q       # 1.76s
# Total: ~5.28 seconds
```

**After (This Fork)**:

```bash
# Single command with concurrent processing
bstrings.exe -f file.txt --lr "guid,email,cc" -q  # 1.39s

# OR search all patterns at once
bstrings.exe -f file.txt --lr all -q              # 5.66s (all 27 patterns!)

# FASTEST: Output to file with suppressed console (NEW!)
bstrings.exe -f file.txt --lr all -q -o results.txt  # Maximum performance!
```

**After (This Fork)**:

```bash
# Single command with concurrent processing
bstrings.exe -f file.txt --lr "guid,email,cc" -q  # 1.39s
# OR search all patterns at once
bstrings.exe -f file.txt --lr all -q              # 5.66s (all 27 patterns!)
```

### Key Benefits

- **🔥 Massive time savings** for multiple pattern searches
- **📈 Scales efficiently** - more patterns don't linearly increase time
- **🛠️ Better workflow** - single command instead of multiple executions
- **🎯 Perfect for forensics** - quickly scan for all artifact types at once

## 🎛️ Enhanced Output Control

This fork includes **intelligent output control** for better performance with large files:

### 🔇 **Smart Console Suppression**

When both `--quiet` (`-q`) and an output file (`-o`) are specified together, console output of found strings is automatically suppressed for maximum performance:

```bash
# FAST: Results written to file, no console output of strings
bstrings.exe -f large_file.bin --lr "email,guid,cc" -q -o results.txt

# vs. SLOWER: Shows all strings in console + writes to file
bstrings.exe -f large_file.bin --lr "email,guid,cc" -o results.txt
```

### 📊 **Output Behavior Matrix**

| Command Flags    | Console String Output       | File Output     | Performance   |
| ---------------- | --------------------------- | --------------- | ------------- |
| _(none)_         | ✅ Full output              | ❌ None         | Standard      |
| `-o file.txt`    | ✅ Full output              | ✅ File written | Standard      |
| `-q`             | ✅ Strings only (no header) | ❌ None         | Standard      |
| `-q -o file.txt` | ❌ **Suppressed**           | ✅ File written | **⚡ Faster** |
| `-s -o file.txt` | ❌ **Suppressed**           | ✅ File written | **⚡ Faster** |

### 🚀 **Performance Benefits**

- **Eliminates console I/O bottleneck** for large result sets
- **Particularly effective** with thousands of pattern matches
- **Maintains full functionality** - only suppresses console display
- **Works with all processing modes** (single patterns, multiple patterns, concurrent processing)

### 💡 **Usage Examples**

```bash
# High-performance bulk analysis (recommended for large files)
bstrings.exe -f evidence.img --lr all -q -o all_artifacts.txt

# Multiple patterns with quiet output
bstrings.exe -f malware.bin --lr "email,guid,bitcoin,url3986" -q -o findings.txt

# Explicit silent mode (equivalent to -q -o combination)
bstrings.exe -f large_data.bin --lr cc -s -o creditcards.txt
```

## 🤖 Automated Builds & Releases

This repository is configured with **GitHub Actions** for automated building and releasing:

### 🔄 **Continuous Integration**

- **Automatic builds** on every commit to `master` branch
- **Cross-compilation** for Windows x64 (self-contained executable)
- **Quality checks** with automated testing (if tests exist)
- **Multi-configuration builds** (Debug + Release)

### 📦 **Automated Releases**

Every commit to `master` automatically creates a new GitHub release with:

- ✅ **Self-contained Windows x64 executable** (no .NET runtime required)
- ✅ **Clean ZIP package** with executable, documentation, and required files
- ✅ **Professional release notes** with performance benchmarks
- ✅ **Version tagging** with timestamp and commit SHA
- ✅ **Usage examples** and installation instructions

### 📥 **Download Latest Release**

- **Latest Release**: Check the [Releases page](../../releases/latest) for the newest build
- **All Releases**: Browse [all releases](../../releases) to find specific versions
- **Artifacts**: Development builds available in [GitHub Actions](../../actions)

### 🏷️ **Release Versioning**

Releases are automatically tagged as: `v{version}-{timestamp}-{commit}`

- Example: `v1.5.3-20251212-164503-a1b2c3d`
- **Version**: From AssemblyInfo.cs
- **Timestamp**: Build date/time
- **Commit**: Short SHA for traceability

### 🛠️ **For Developers**

The automated workflow:

1. **Builds** both Debug and Release configurations
2. **Tests** automatically (when test projects exist)
3. **Publishes** optimized Windows x64 binary
4. **Packages** with documentation and creates release
5. **Deploys** to GitHub Releases automatically

No manual intervention required - just push to `master` and get a release!

# bstrings

A better strings utility!

## Command Line Interface

    bstrings version 1.5.1.0

    Author: Eric Zimmerman (saericzimmerman@gmail.com)
    https://github.com/EricZimmerman/bstrings

    a               If set, look for ASCII strings. Default is true. Use -a false to disable
            b               Chunk size in MB. Valid range is 1 to 1024. Default is 512
            d               Directory to recursively process. Either this or -f is required
            f               File to search. Either this or -d is required
            m               Minimum string length. Default is 3
            o               File to save results to
            p               Display list of built in regular expressions
            q               Quiet mode (Do not show header or total number of hits)
            s               Really Quiet mode (Do not display hits to console. Speeds up processing when using -o)
            u               If set, look for Unicode strings. Default is true. Use -u false to disable
            x               Maximum string length. Default is unlimited

    ls              String to look for. When set, only matching strings are returned
            lr              Regex to look for. When set, only strings matching the regex are returned
            fs              File containing strings to look for. When set, only matching strings are returned
            fr              File containing regex patterns to look for. When set, only strings matching regex patterns are returned

    ar              Range of characters to search for in 'Code page' strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\x20 -\x7E]
            ur              Range of characters to search for in Unicode strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\u0020-\u007E]

    cp              Code page to use. Default is 1252. Use the Identifier value for code pages at https://goo.gl/ig6DxW
            mask            When using -d, file mask to search for. * and ? are supported. This option has no effect when using -f
            ms              When using -d, maximum file size to process. This option has no effect when using -f
            ro              When true, list the string matched by regex pattern vs string the pattern was found in (This may result in duplicate strings in output. ~ denotes approx. offset)
            off             Show offset to hit after string, followed by the encoding (A=1252, U=Unicode)    sa              Sort results alphabetically
            sl              Sort results by length

## Examples

### Basic Usage

```bash
bstrings.exe -f "C:\Temp\UsrClass 1.dat" --ls URL
bstrings.exe -f "C:\Temp\someFile.txt" --lr guid
bstrings.exe -f "C:\Temp\someFile.txt" --lr "guid,email,cc"
bstrings.exe -f "C:\Temp\someFile.txt" --lr all
```

### High-Performance Output Control (NEW!)

```bash
# Fast: Quiet mode + output file = suppressed console output
bstrings.exe -f "C:\Evidence\large.img" --lr all -q -o artifacts.txt

# Fast: Explicit silent mode
bstrings.exe -f "C:\Malware\sample.bin" --lr "email,bitcoin,url3986" -s -o findings.txt

# Standard: Regular output (slower with large result sets)
bstrings.exe -f "C:\Data\file.bin" --lr "guid,cc" -o results.txt
```

### Advanced Usage

```bash
bstrings.exe -f "C:\Temp\aBigFile.bin" --fs c:\temp\searchStrings.txt --fr c:\temp\searchRegex.txt -s
bstrings.exe -d "C:\Temp" --mask "*.dll"
bstrings.exe -d "C:\Temp" --ar "[\x20-\x37]"
bstrings.exe -d "C:\Temp" --cp 10007
bstrings.exe -d "C:\Temp" --ls test
bstrings.exe -f "C:\Temp\someOtherFile.txt" --lr cc --sa
bstrings.exe -f "C:\Temp\someOtherFile.txt" --lr cc --sa -m 15 -x 22
bstrings.exe -f "C:\Temp\UsrClass 1.dat" --ls mui --sl
```

## Built In Regular Expressions

Run `bstrings.exe -p` to see the following list of built in Regular Expressions:

    Name            Description
              aeon            Finds Aeon wallet addresses
              b64             Finds valid formatted base 64 strings
              bitcoin         Finds BitCoin wallet addresses
              bitlocker       Finds Bitlocker recovery keys
              bytecoin        Finds ByteCoin wallet addresses
              cc              Finds credit card numbers
              dashcoin        Finds DashCoin wallet addresses (D*)
              dashcoin2       Finds DashCoin wallet addresses (7|X)*
              email           Finds embedded email addresses
              fantomcoin      Finds Fantomcoin wallet addresses
              guid            Finds GUIDs
              ipv4            Finds IP version 4 addresses
              ipv6            Finds IP version 6 addresses
              mac             Finds MAC addresses
              monero          Finds Monero wallet addresses
              reg_path        Finds paths related to Registry hives
              sid             Finds Microsoft Security Identifiers (SID)
              ssn             Finds US Social Security Numbers
              sumokoin        Finds SumoKoin wallet addresses
              unc             Finds UNC paths
              url3986         Finds URLs according to RFC 3986
              urlUser         Finds usernames in URLs
              usPhone         Finds US phone numbers
              var_set         Finds environment variables being set (OS=Windows_NT)
              win_path        Finds Windows style paths (C:\folder1\folder2\file.txt)
              xml             Finds XML/HTML tags
              zip             Finds zip codes

    To use a built in pattern, supply the Name to the --lr switch

## Documentation

[Introducing bstrings, a Better Strings utility!](https://binaryforay.blogspot.com/2015/07/introducing-bstrings-better-strings.html)

[bstrings 0.9.0.0 released](https://binaryforay.blogspot.com/2015/07/bstrings-0900-released.html)

[bstrings 0.9.5.0 released](https://binaryforay.blogspot.com/2015/07/bstrings-0950-released.html)

[A few updates](https://binaryforay.blogspot.com/2015/08/a-few-updates.html)

[bstrings 0.9.7.0 released](https://binaryforay.blogspot.com/2015/11/bstrings-0970-released.html)

[bstrings 0.9.8.0 released](https://binaryforay.blogspot.com/2015/12/bstrings-0980-released.html)

[bstrings 0.9.9.0 released!](https://binaryforay.blogspot.com/2016/02/bstrings-0990-released.html)

[bstrings 1.0 released!](https://binaryforay.blogspot.com/2016/02/bstrings-10-released.html)

[bstrings v1.1 released!](https://binaryforay.blogspot.com/2016/04/bstrings-v11-released.html)

[Everything gets an update, Sept 2018 edition](https://binaryforay.blogspot.com/2018/09/everything-gets-update-sept-2018-edition.html?q=bstrings)

# Download Eric Zimmerman's Tools

All of Eric Zimmerman's tools can be downloaded [here](https://ericzimmerman.github.io/#!index.md).

# Special Thanks

Open Source Development funding and support provided by the following contributors: [SANS Institute](http://sans.org/) and [SANS DFIR](http://dfir.sans.org/).
