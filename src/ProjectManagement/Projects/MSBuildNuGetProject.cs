﻿using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.PackagingCore;
using NuGet.ProjectManagement;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NuGet.ProjectManagement
{
    /// <summary>
    /// This class represents a NuGetProject based on a .NET project. This also contains an instance of a FolderNuGetProject
    /// </summary>
    public class MSBuildNuGetProject : NuGetProject
    {
        private IMSBuildNuGetProjectSystem MSBuildNuGetProjectSystem { get; set; }
        public FolderNuGetProject FolderNuGetProject { get; private set; }
        public PackagesConfigNuGetProject PackagesConfigNuGetProject { get; private set; }
        public MSBuildNuGetProject(IMSBuildNuGetProjectSystem msbuildNuGetProjectSystem, string folderNuGetProjectPath, string packagesConfigFullPath)
        {
            if (msbuildNuGetProjectSystem == null)
            {
                throw new ArgumentNullException("nugetDotNetProjectSystem");
            }

            if (folderNuGetProjectPath == null)
            {
                throw new ArgumentNullException("folderNuGetProjectPath");
            }

            if (packagesConfigFullPath == null)
            {
                throw new ArgumentNullException("packagesConfigFullPath");
            }

            MSBuildNuGetProjectSystem = msbuildNuGetProjectSystem;
            FolderNuGetProject = new FolderNuGetProject(folderNuGetProjectPath);
            InternalMetadata.Add(NuGetProjectMetadataKeys.Name, MSBuildNuGetProjectSystem.ProjectName);
            InternalMetadata.Add(NuGetProjectMetadataKeys.TargetFramework, MSBuildNuGetProjectSystem.TargetFramework);            
            PackagesConfigNuGetProject = new PackagesConfigNuGetProject(packagesConfigFullPath, InternalMetadata);
        }

        public override IEnumerable<PackageReference> GetInstalledPackages()
        {
            return PackagesConfigNuGetProject.GetInstalledPackages();
        }

        public override bool InstallPackage(PackageIdentity packageIdentity, Stream packageStream, INuGetProjectContext nuGetProjectContext)
        {
            if(packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if(packageStream == null)
            {
                throw new ArgumentNullException("packageStream");
            }

            if(nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // Step-0: Check if the package already exists after setting the nuGetProjectContext
            MSBuildNuGetProjectSystem.SetNuGetProjectContext(nuGetProjectContext);

            var packageReference = PackagesConfigNuGetProject.InstalledPackagesList.Where(
                p => p.PackageIdentity.Equals(packageIdentity)).FirstOrDefault();
            if (packageReference != null)
            {
                nuGetProjectContext.Log(MessageLevel.Warning, Strings.PackageAlreadyExistsInProject,
                    packageIdentity, MSBuildNuGetProjectSystem.ProjectName);
                return false;
            }

            // Step-1: Create PackageReader using the PackageStream and obtain the various item groups
            PackageReader packageReader = new PackageReader(new ZipArchive(packageStream));
            IEnumerable<FrameworkSpecificGroup> libItemGroups = packageReader.GetLibItems();
            IEnumerable<FrameworkSpecificGroup> frameworkReferenceGroups = packageReader.GetFrameworkItems();
            IEnumerable<FrameworkSpecificGroup> contentFileGroups = packageReader.GetContentItems();
            IEnumerable<FrameworkSpecificGroup> buildFileGroups = packageReader.GetBuildItems();

            // Step-2: Get the most compatible items groups for all items groups
            bool hasCompatibleItems = false;

            FrameworkSpecificGroup compatibleLibItemsGroup =
                GetMostCompatibleGroup(MSBuildNuGetProjectSystem.TargetFramework, libItemGroups);
            FrameworkSpecificGroup compatibleFrameworkReferencesGroup =
                GetMostCompatibleGroup(MSBuildNuGetProjectSystem.TargetFramework, frameworkReferenceGroups);
            FrameworkSpecificGroup compatibleContentFilesGroup =
                GetMostCompatibleGroup(MSBuildNuGetProjectSystem.TargetFramework, contentFileGroups);
            FrameworkSpecificGroup compatibleBuildFilesGroup =
                GetMostCompatibleGroup(MSBuildNuGetProjectSystem.TargetFramework, buildFileGroups);            

            hasCompatibleItems = IsValid(compatibleLibItemsGroup) || IsValid(compatibleFrameworkReferencesGroup) ||
                IsValid(compatibleContentFilesGroup) && IsValid(compatibleBuildFilesGroup);

            // Step-3: Check if there are any compatible items in the package. If not, throw
            if(!hasCompatibleItems)
            {
                throw new InvalidOperationException(
                           String.Format(CultureInfo.CurrentCulture,
                           Strings.UnableToFindCompatibleItems, packageIdentity, MSBuildNuGetProjectSystem.TargetFramework));
            }

            // Step-4: FolderNuGetProject.InstallPackage(packageIdentity, packageStream);
            FolderNuGetProject.InstallPackage(packageIdentity, packageStream, nuGetProjectContext);

            // Step-5: Update packages.config
            PackagesConfigNuGetProject.InstallPackage(packageIdentity, packageStream, nuGetProjectContext);

            // Step-6: MSBuildNuGetProjectSystem operations
            // Step-6.1: Add references to project
            foreach (var libItem in compatibleLibItemsGroup.Items)
            {
                if(IsAssemblyReference(libItem))
                {
                    var libItemFullPath = Path.Combine(FolderNuGetProject.PackagePathResolver.GetInstallPath(packageIdentity), libItem);
                    MSBuildNuGetProjectSystem.AddReference(libItemFullPath);
                }
            }

            // Step-6.2: Add Frameworkreferences to project
            foreach(var frameworkReference in compatibleFrameworkReferencesGroup.Items)
            {
                if(IsAssemblyReference(frameworkReference))
                {
                    MSBuildNuGetProjectSystem.AddReference(Path.GetFileName(frameworkReference));
                }
            }

            // Step-6.3: Add Content Files

            // Step-6.4: Add Build imports
            
            // Step-6.5: Execute powershell script

            return true;
        }

        public override bool UninstallPackage(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext)
        {
            if (packageIdentity == null)
            {
                throw new ArgumentNullException("packageIdentity");
            }

            if (nuGetProjectContext == null)
            {
                throw new ArgumentNullException("nuGetProjectContext");
            }

            // Step-0: Check if the package already exists after setting the nuGetProjectContext
            MSBuildNuGetProjectSystem.SetNuGetProjectContext(nuGetProjectContext);

            var packageReference = PackagesConfigNuGetProject.InstalledPackagesList.Where(
                p => p.PackageIdentity.Equals(packageIdentity)).FirstOrDefault();
            if (packageReference == null)
            {
                nuGetProjectContext.Log(MessageLevel.Warning, Strings.PackageDoesNotExistInProject,
                    packageIdentity, MSBuildNuGetProjectSystem.ProjectName);
                return false;
            }

            throw new NotImplementedException();
        }

        protected static FrameworkSpecificGroup GetMostCompatibleGroup(NuGetFramework projectTargetFramework, IEnumerable<FrameworkSpecificGroup> itemGroups)
        {
            FrameworkReducer reducer = new FrameworkReducer();
            NuGetFramework mostCompatibleFramework = reducer.GetNearest(projectTargetFramework, itemGroups.Select(i => NuGetFramework.Parse(i.TargetFramework)));
            if(mostCompatibleFramework != null)
            {
                IEnumerable<FrameworkSpecificGroup> mostCompatibleGroups = itemGroups.Where(i => NuGetFramework.Parse(i.TargetFramework).Equals(mostCompatibleFramework));
                return mostCompatibleGroups.FirstOrDefault();
            }
            return null;
        }

        private bool IsValid(FrameworkSpecificGroup frameworkSpecificGroup)
        {
            // It is possible for a compatible frameworkSpecificGroup 
            return (frameworkSpecificGroup != null && frameworkSpecificGroup.Items != null);
        }

        private void LogTargetFrameworkInfo(PackageIdentity packageIdentity, INuGetProjectContext nuGetProjectContext,
            FrameworkSpecificGroup libItemsGroup, FrameworkSpecificGroup contentItemsGroup, FrameworkSpecificGroup buildFilesGroup)
        {
            if(libItemsGroup.Items.Any() || contentItemsGroup.Items.Any() || buildFilesGroup.Items.Any())
            {
                string shortFramework = MSBuildNuGetProjectSystem.TargetFramework.GetShortFolderName();
                nuGetProjectContext.Log(MessageLevel.Debug, Strings.Debug_TargetFrameworkInfoPrefix, packageIdentity,
                    this.GetMetadata<string>(NuGetProjectMetadataKeys.Name), shortFramework);
            }
        }

        private static string GetTargetFrameworkLogString(NuGetFramework targetFramework)
        {
            return (targetFramework == null || targetFramework == NuGetFramework.AnyFramework) ? Strings.Debug_TargetFrameworkInfo_NotFrameworkSpecific : String.Empty;
        }

        private static bool IsAssemblyReference(string filePath)
        {
            // assembly reference must be under lib/
            if (!filePath.StartsWith(Constants.LibDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fileName = Path.GetFileName(filePath);

            // if it's an empty folder, yes
            if (fileName == Constants.PackageEmptyFileName)
            {
                return true;
            }

            // Assembly reference must have a .dll|.exe|.winmd extension and is not a resource assembly;
            return !filePath.EndsWith(Constants.ResourceAssemblyExtension, StringComparison.OrdinalIgnoreCase) &&
                Constants.AssemblyReferencesExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class Constants
    {
        /// <summary>
        /// Represents the ".nupkg" extension.
        /// </summary>
        public static readonly string PackageExtension = ".nupkg";

        /// <summary>
        /// Represents the ".nuspec" extension.
        /// </summary>
        public static readonly string ManifestExtension = ".nuspec";

        /// <summary>
        /// Represents the content directory in the package.
        /// </summary>
        public static readonly string ContentDirectory = "content";

        /// <summary>
        /// Represents the lib directory in the package.
        /// </summary>
        public static readonly string LibDirectory = "lib";

        /// <summary>
        /// Represents the tools directory in the package.
        /// </summary>
        public static readonly string ToolsDirectory = "tools";

        /// <summary>
        /// Represents the build directory in the package.
        /// </summary>
        public static readonly string BuildDirectory = "build";

        public static readonly string BinDirectory = "bin";
        public static readonly string SettingsFileName = "NuGet.Config";
        public static readonly string PackageReferenceFile = "packages.config";
        public static readonly string MirroringReferenceFile = "mirroring.config";

        public static readonly string BeginIgnoreMarker = "NUGET: BEGIN LICENSE TEXT";
        public static readonly string EndIgnoreMarker = "NUGET: END LICENSE TEXT";

        internal const string PackageRelationshipNamespace = "http://schemas.microsoft.com/packaging/2010/07/";

        // Starting from nuget 2.0, we use a file with the special name '_._' to represent an empty folder.
        internal const string PackageEmptyFileName = "_._";

        // This is temporary until we fix the gallery to have proper first class support for this.
        // The magic unpublished date is 1900-01-01T00:00:00
        public static readonly DateTimeOffset Unpublished = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.FromHours(-8));

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Security",
            "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes",
            Justification = "The type is immutable.")]
        public static readonly ICollection<string> AssemblyReferencesExtensions
            = new ReadOnlyCollection<string>(new string[] { ".dll", ".exe", ".winmd" });

        public const string ResourceAssemblyExtension = ".resources.dll";
    }
}
