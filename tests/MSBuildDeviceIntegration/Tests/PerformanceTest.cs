using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NUnit.Framework;
using Xamarin.ProjectTools;

namespace Xamarin.Android.Build.Tests
{
	[TestFixture, NonParallelizable]
	public class PerformanceTest : DeviceTest
	{
		static readonly Dictionary<string, int> csv_values = new Dictionary<string, int> ();

		[OneTimeSetUp]
		public static void Setup ()
		{
			var csv = Path.Combine (XABuildPaths.TopDirectory, "tests", "msbuild-times-reference", "MSBuildDeviceIntegration.csv");
			using (var reader = File.OpenText (csv)) {
				bool foundHeader = false;
				while (!reader.EndOfStream) {
					var line = reader.ReadLine ();
					if (line.StartsWith ("#") || string.IsNullOrWhiteSpace (line)) {
						continue;
					}
					var split = line.Split (',');
					Assert.AreEqual (2, split.Length, $"{csv} should have two entries per line.");
					if (!foundHeader) {
						// Ignore the first-line header
						foundHeader = true;
						continue;
					}
					string text = split [1];
					if (int.TryParse (text, out int value)) {
						csv_values [split [0]] = value;
					} else {
						Assert.Fail ($"'{text}' is not a valid integer!");
					}
				}
			}
		}

		void Profile (ProjectBuilder builder, Action<ProjectBuilder> action, [CallerMemberName] string caller = null)
		{
			if (!csv_values.TryGetValue (caller, out int expected)) {
				Assert.Fail ($"No timeout value found for a key of {caller}");
			}

			action (builder);
			var actual = GetDurationFromBinLog (builder);
			TestContext.Out.WriteLine($"expected: {expected}ms, actual: {actual}ms");
			if (actual > expected) {
				Assert.Fail ($"Exceeded expected time of {expected}ms, actual {actual}ms");
			}
		}

		double GetDurationFromBinLog (ProjectBuilder builder)
		{
			var duration = TimeSpan.Zero;
			var binlog = Path.Combine (Root, builder.ProjectDirectory, "msbuild.binlog");
			FileAssert.Exists (binlog);

			using (var fileStream = File.OpenRead (binlog))
			using (var gzip = new GZipStream (fileStream, CompressionMode.Decompress))
			using (var binaryReader = new BinaryReader (gzip)) {
				int fileFormatVersion = binaryReader.ReadInt32 ();
				var buildReader = new BuildEventArgsReader (binaryReader, fileFormatVersion);
				BuildEventArgs args;
				var started = new Stack<DateTime> ();
				while ((args = buildReader.Read ()) != null) {
					if (args is ProjectStartedEventArgs projectStarted) {
						started.Push (projectStarted.Timestamp);
					} else if (args is ProjectFinishedEventArgs projectFinished) {
						duration += projectFinished.Timestamp - started.Pop ();
					}
				}
			}

			if (duration == TimeSpan.Zero)
				throw new InvalidDataException ($"No project build duration found in {binlog}");

			return duration.TotalMilliseconds;
		}

		ProjectBuilder CreateBuilderWithoutLogFile (string directory = null)
		{
			var builder = CreateApkBuilder (directory);
			builder.BuildLogFile = null;
			builder.Verbosity = LoggerVerbosity.Quiet;
			return builder;
		}

		[Test]
		public void Build_From_Clean_DontIncludeRestore ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			proj.MainActivity = proj.DefaultMainActivity;
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Restore (proj);
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_No_Changes ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			proj.MainActivity = proj.DefaultMainActivity;
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile no changes
				Profile (builder, b => b.Build (proj));

				// Change C# and build
				proj.MainActivity += $"{Environment.NewLine}//comment";
				proj.Touch ("MainActivity.cs");
				builder.Build (proj);

				// Profile no changes
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_CSharp_Change ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			proj.MainActivity = proj.DefaultMainActivity;
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile C# change
				proj.MainActivity += $"{Environment.NewLine}//comment";
				proj.Touch ("MainActivity.cs");
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_AndroidResource_Change ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile AndroidResource change
				proj.LayoutMain += $"{Environment.NewLine}<!--comment-->";
				proj.Touch ("Resources\\layout\\Main.axml");
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_Designer_Change ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			proj.MainActivity = proj.DefaultMainActivity;
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Change AndroidResource & run SetupDependenciesForDesigner
				proj.LayoutMain += $"{Environment.NewLine}<!--comment-->";
				proj.Touch ("Resources\\layout\\Main.axml");
				var parameters = new [] { "DesignTimeBuild=True", "AndroidUseManagedDesignTimeResourceGenerator=False" };
				builder.RunTarget (proj, "SetupDependenciesForDesigner", parameters: parameters);

				// Profile AndroidResource change
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_JLO_Change ()
		{
			var className = "Foo";
			var proj = new XamarinAndroidApplicationProject ();
			proj.Sources.Add (new BuildItem.Source ("Foo.cs") {
				TextContent = () => $"class {className} : Java.Lang.Object {{}}"
			});
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile Java.Lang.Object rename
				className = "Foo2";
				proj.Touch ("Foo.cs");
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_AndroidManifest_Change ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile AndroidManifest.xml change
				proj.AndroidManifest += $"{Environment.NewLine}<!--comment-->";
				proj.Touch ("Properties\\AndroidManifest.xml");
				Profile (builder, b => b.Build (proj));
			}
		}

		[Test]
		public void Build_CSProj_Change ()
		{
			var proj = new XamarinAndroidApplicationProject ();
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Target = "Build";
				builder.Build (proj);

				// Profile .csproj change
				proj.Sources.Add (new BuildItem ("None", "Foo.txt") {
					TextContent = () => "Bar",
				});
				Profile (builder, b => b.Build (proj));
			}
		}

		static object [] XAML_Change = new object [] {
			new object [] {
				/* produceReferenceAssembly */ false,
				/* install */                  false,
			},
			new object [] {
				/* produceReferenceAssembly */ true,
				/* install */                  false,
			},
			new object [] {
				/* produceReferenceAssembly */ true,
				/* install */                  true,
			},
		};

		[Test]
		[TestCaseSource (nameof (XAML_Change))]
		[Category ("UsesDevice")]
		public void Build_XAML_Change (bool produceReferenceAssembly, bool install)
		{
			if (install) {
				AssertCommercialBuild (); // This test will fail without Fast Deployment
				AssertHasDevices ();
			}

			var path = Path.Combine ("temp", TestName);
			var xaml =
@"<?xml version=""1.0"" encoding=""utf-8"" ?>
<ContentPage xmlns=""http://xamarin.com/schemas/2014/forms""
             xmlns:x=""http://schemas.microsoft.com/winfx/2009/xaml""
             x:Class=""MyLibrary.MyPage"">
</ContentPage>";
			var caller = nameof (Build_XAML_Change);
			if (install) {
				caller = caller.Replace ("Build", "Install");
			} else if (produceReferenceAssembly) {
				caller += "_RefAssembly";
			}
			var app = new XamarinFormsAndroidApplicationProject {
				ProjectName = "MyApp",
				Sources = {
					new BuildItem.Source ("Foo.cs") {
						TextContent = () => "public class Foo : Bar { }"
					},
				}
			};
			//NOTE: this will skip a 382ms <VerifyVersionsTask/> from the support library
			app.SetProperty ("XamarinAndroidSupportSkipVerifyVersions", "True");

			int count = 0;
			var lib = new DotNetStandard {
				ProjectName = "MyLibrary",
				Sdk = "Microsoft.NET.Sdk",
				TargetFramework = "netstandard2.0",
				Sources = {
					new BuildItem.Source ("Bar.cs") {
						TextContent = () => "public class Bar { public Bar () { System.Console.WriteLine (" + count++ + "); } }"
					},
					new BuildItem ("EmbeddedResource", "MyPage.xaml") {
						TextContent = () => xaml,
					}
				},
				PackageReferences = {
					KnownPackages.XamarinForms_4_0_0_425677
				}
			};
			lib.SetProperty ("ProduceReferenceAssembly", produceReferenceAssembly.ToString ());
			app.References.Add (new BuildItem.ProjectReference ($"..\\{lib.ProjectName}\\{lib.ProjectName}.csproj", lib.ProjectName, lib.ProjectGuid));

			using (var libBuilder = CreateDllBuilder (Path.Combine (path, lib.ProjectName)))
			using (var appBuilder = CreateBuilderWithoutLogFile (Path.Combine (path, app.ProjectName))) {
				libBuilder.Build (lib);
				appBuilder.Target = "Build";
				if (install) {
					appBuilder.Install (app);
				} else {
					appBuilder.Build (app);
				}

				libBuilder.AutomaticNuGetRestore =
					appBuilder.AutomaticNuGetRestore = false;

				// Profile XAML change
				xaml += $"{Environment.NewLine}<!--comment-->";
				lib.Touch ("MyPage.xaml");
				libBuilder.Build (lib, doNotCleanupOnUpdate: true);
				if (install) {
					Profile (appBuilder, b => b.Install (app, doNotCleanupOnUpdate: true), caller);
				} else {
					Profile (appBuilder, b => b.Build (app, doNotCleanupOnUpdate: true), caller);
				}
			}
		}

		[Test]
		[Category ("UsesDevice")]
		public void Install_CSharp_Change ()
		{
			AssertCommercialBuild (); // This test will fail without Fast Deployment
			AssertHasDevices ();

			var proj = new XamarinAndroidApplicationProject ();
			proj.MainActivity = proj.DefaultMainActivity;
			using (var builder = CreateBuilderWithoutLogFile ()) {
				builder.Install (proj);

				// Profile C# change
				proj.MainActivity += $"{Environment.NewLine}//comment";
				proj.Touch ("MainActivity.cs");
				Profile (builder, b => b.Install (proj));
			}
		}
	}
}
