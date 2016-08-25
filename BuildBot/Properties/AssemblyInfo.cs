using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommandLine.Text;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyCompany("Jesse Nicholson")]
[assembly: AssemblyProduct("BuildBot")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyLicense(
    "\n\nThis is free software. You may redistribute copies of it under the terms of",
    "the MIT License <http://www.opensource.org/licenses/mit-license.php>.")]
[assembly: AssemblyTitle("BuildBot.dll")]
[assembly: AssemblyDescription("BuildBot is a .NET core based portable application for building complex projects simply in typed, standard C# code.")]
#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("f15c3cc7-c01f-4bc8-a1cb-7c4ebceee8bf")]
