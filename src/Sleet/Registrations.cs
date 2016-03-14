﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Sleet
{
    public class Registrations : ISleetService
    {
        private readonly SleetContext _context;

        public Registrations(SleetContext context)
        {
            _context = context;
        }

        public async Task AddPackage(PackageInput package)
        {
            // Retrieve index
            var rootUri = GetIndexUri(package.Identity);
            var rootFile = _context.Source.Get(rootUri);

            var packages = new List<JObject>();

            if (await rootFile.Exists(_context.Log, _context.Token))
            {
                var json = await rootFile.GetJson(_context.Log, _context.Token);

                // Get all entries
                packages = await GetPackageDetails(json);
            }

            // Add entry
            var newEntry = await CreateItem(package);
            packages.Add(newEntry);

            // Create index
            var newIndexJson = CreateIndex(rootUri, packages);

            // Write
            await rootFile.Write(newIndexJson, _context.Log, _context.Token);

            // Create package page
            var packageUri = GetPackageUri(package.Identity);
            var packageFile = _context.Source.Get(packageUri);

            var packageJson = await CreatePackageBlob(package);

            // Write package page
            await packageFile.Write(packageJson, _context.Log, _context.Token);
        }

        public async Task<bool> RemovePackage(PackageIdentity package)
        {
            var found = false;

            // Retrieve index
            var rootUri = GetIndexUri(package);
            var rootFile = _context.Source.Get(rootUri);

            var packages = new List<JObject>();

            if (await rootFile.Exists(_context.Log, _context.Token))
            {
                var json = await rootFile.GetJson(_context.Log, _context.Token);

                // Get all entries
                packages = await GetPackageDetails(json);

                foreach (var entry in packages.ToArray())
                {
                    var version = GetPackageVersion(entry);

                    if (version == package.Version)
                    {
                        found = true;
                        packages.Remove(entry);
                    }
                }
            }

            if (found)
            {
                // Delete package page
                var packageUri = GetPackageUri(package);
                var packageFile = _context.Source.Get(packageUri);

                if (await packageFile.Exists(_context.Log, _context.Token))
                {
                    packageFile.Delete(_context.Log, _context.Token);
                }

                if (packages.Count > 0)
                {
                    // Create index
                    var newIndexJson = CreateIndex(rootUri, packages);

                    // Write
                    await rootFile.Write(newIndexJson, _context.Log, _context.Token);
                }
                else
                {
                    // This package id been completely removed
                    rootFile.Delete(_context.Log, _context.Token);
                }
            }

            return found;
        }

        /// <summary>
        /// Get all package details from all pages
        /// </summary>
        public Task<List<JObject>> GetPackageDetails(JObject json)
        {
            var pages = GetItems(json);
            return Task.FromResult(pages.SelectMany(GetItems).ToList());
        }

        public JObject CreateIndex(Uri indexUri, List<JObject> packageDetails)
        {
            var json = JsonUtility.Create(indexUri,
                new string[] {
                    "catalog:CatalogRoot",
                    "PackageRegistration",
                    "catalog:Permalink"
                });

            json.Add("commitId", _context.CommitId.ToString().ToLowerInvariant());
            json.Add("commitTimeStamp", _context.Now.GetDateString());

            // Add everything to a single page
            var pageJson = CreatePage(indexUri, packageDetails);

            var context = JsonUtility.GetContext("Registration");
            json.Add("@context", context);

            return JsonLDTokenComparer.Format(json);
        }

        public JObject CreatePage(Uri indexUri, List<JObject> packageDetails)
        {
            var versionSet = new HashSet<NuGetVersion>(packageDetails.Select(GetPackageVersion));
            var lower = versionSet.Min().ToNormalizedString().ToLowerInvariant();
            var upper = versionSet.Max().ToNormalizedString().ToLowerInvariant();

            var json = JsonUtility.Create(indexUri, $"page/{lower}/{upper}", "catalog:CatalogPage");

            json.Add("commitId", _context.CommitId.ToString().ToLowerInvariant());
            json.Add("commitTimeStamp", _context.Now.GetDateString());

            json.Add("count", packageDetails.Count);

            json.Add("parent", indexUri.AbsoluteUri);
            json.Add("lower", lower);
            json.Add("upper", upper);

            var itemsArray = new JArray();
            json.Add("items", itemsArray);

            // Order and add all items
            foreach (var entry in packageDetails.OrderBy(GetPackageVersion))
            {
                itemsArray.Add(entry);
            }

            return JsonLDTokenComparer.Format(json);
        }

        public static NuGetVersion GetPackageVersion(JObject packageDetails)
        {
            var catalogEntry = (JObject)packageDetails["catalogEntry"];
            var version = NuGetVersion.Parse(catalogEntry.Property("version").Value.ToString());

            return version;
        }

        /// <summary>
        /// Get items from a page or index page.
        /// </summary>
        public static List<JObject> GetItems(JObject json)
        {
            var result = new List<JObject>();
            var items = json["items"] as JArray;

            if (items != null)
            {
                foreach (var item in items)
                {
                    result.Add((JObject)item);
                }
            }

            return result;
        }

        public Uri GetIndexUri(PackageIdentity package)
        {
            return new Uri($"{_context.Source.Root}registation/{package.Id.ToLowerInvariant()}/index.json");
        }

        public Uri GetPackageUri(PackageIdentity package)
        {
            return new Uri($"{_context.Source.Root}registation/{package.Id.ToLowerInvariant()}/{package.Version.ToNormalizedString().ToLowerInvariant()}.json");
        }

        public async Task<JObject> CreatePackageBlob(PackageInput packageInput)
        {
            var rootUri = GetPackageUri(packageInput.Identity);

            var json = JsonUtility.Create(rootUri, new string[] { "Package", "http://schema.nuget.org/catalog#Permalink" });

            var packageDetailsFile = _context.Source.Get(packageInput.PackageDetailsUri);
            var detailsJson = await packageDetailsFile.GetJson(_context.Log, _context.Token);

            json.Add("catalogEntry", packageInput.PackageDetailsUri.AbsoluteUri);
            json.Add("packageContent", detailsJson["packageContent"].ToString());
            json.Add("registration", GetIndexUri(packageInput.Identity));

            var copyProperties = new List<string>()
            {
                "listed",
                "published",
            };

            foreach (var fieldName in copyProperties)
            {
                var catalogProperty = detailsJson[fieldName];

                if (catalogProperty != null)
                {
                    json.Add(catalogProperty);
                }
            }

            var context = JsonUtility.GetContext("Package");
            json.Add("@context", context);

            return JsonLDTokenComparer.Format(json);
        }

        public async Task<JObject> CreateItem(PackageInput packageInput)
        {
            var rootUri = GetPackageUri(packageInput.Identity);

            var json = JsonUtility.Create(rootUri, "Package");
            json.Add("commitId", _context.CommitId.ToString().ToLowerInvariant());
            json.Add("commitTimeStamp", _context.Now.GetDateString());

            var packageDetailsFile = _context.Source.Get(packageInput.PackageDetailsUri);
            var detailsJson = await packageDetailsFile.GetJson(_context.Log, _context.Token);

            json.Add("packageContent", detailsJson["packageContent"].ToString());
            json.Add("registration", GetIndexUri(packageInput.Identity));

            var copyProperties = new List<string>()
            {
                "@id",
                "@type",
                "authors",
                "dependencyGroups",
                "description",
                "iconUrl",
                "id",
                "language",
                "licenseUrl",
                "listed",
                "minClientVersion",
                "packageContent",
                "projectUrl",
                "published",
                "requiredLicenseAcceptance",
                "summary",
                "tags",
                "title",
                "version"
            };

            var catalogEntry = new JObject();
            json.Add("catalogEntry", catalogEntry);

            foreach (var fieldName in copyProperties)
            {
                var catalogProperty = detailsJson[fieldName];

                if (catalogProperty != null)
                {
                    catalogEntry.Add(catalogProperty);
                }
            }

            catalogEntry = JsonLDTokenComparer.Format(catalogEntry);

            return JsonLDTokenComparer.Format(json);
        }
    }
}