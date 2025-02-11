using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CharacterMap.Helpers;
using CharacterMap.Models;
using CharacterMapCX;
using Microsoft.Toolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Mvvm.Messaging;
using Windows.Storage;
using Windows.Storage.Search;
using Windows.UI.Text;
using WoffToOtf;

namespace CharacterMap.Core
{
    public class FontImportResult
    {
        public FontImportResult(List<StorageFile> imported, List<StorageFile> existing, List<(IStorageItem, string)> invalid)
        {
            Imported = imported;
            Existing = existing;
            Invalid = invalid;
        }

        public List<StorageFile> Imported { get; }
        public List<StorageFile> Existing { get; }
        public List<(IStorageItem, string)> Invalid { get; }
    }

    public class FontFinder
    {
        private const string PENDING = nameof(PENDING);
        private const string TEMP = nameof(TEMP);

        private static SemaphoreSlim _initSemaphore                 { get; } = new (1,1);
        private static SemaphoreSlim _loadSemaphore                 { get; } = new (1,1);

        /* If we can't delete a font during a session, we mark it here */
        private static HashSet<string> _ignoredFonts                { get; } = new ();

        private static StorageFolder _importFolder                  => ApplicationData.Current.LocalFolder;

        public static IReadOnlyList<InstalledFont> Fonts            { get; private set; }
        public static IReadOnlyList<InstalledFont> ImportedFonts    { get; private set; }

        public static InstalledFont DefaultFont                     { get; private set; }
        public static bool HasAppxFonts                             { get; private set; }
        public static bool HasRemoteFonts                           { get; private set; }
        public static bool HasVariableFonts                         { get; private set; }

        public static DWriteFallbackFont Fallback                   { get; private set; }

        public static HashSet<string> SupportedFormats              { get; } = new ()
        {
            ".ttf", ".otf", ".otc", ".ttc", // ".woff", ".woff2"
        };

        public static HashSet<string> ImportFormats                 { get; } = new ()
        {
            ".ttf", ".otf", ".otc", ".ttc", ".woff", ".zip"//, ".woff2"
        };

        public static async Task<DWriteFontSet> InitialiseAsync()
        {
            await _initSemaphore.WaitAsync().ConfigureAwait(false);

            var interop = Ioc.Default.GetService<NativeInterop>();
            var systemFonts = interop.GetSystemFonts();

            try
            {
                if (DefaultFont == null)
                {
                    await Task.WhenAll(
                        CleanUpTempFolderAsync(),
                        CleanUpPendingDeletesAsync()).ConfigureAwait(false);

                    var segoe = systemFonts.Fonts.FirstOrDefault(
                           f => f.FontFace.FamilyNames.Values.Contains("Segoe UI")
                        && f.FontFace.Weight.Weight == FontWeights.Normal.Weight
                        && f.FontFace.Stretch == FontStretch.Normal
                        && f.FontFace.Style == FontStyle.Normal);

                    if (segoe != null)
                        DefaultFont = InstalledFont.CreateDefault(segoe);
                }
            }
            finally
            {
                _initSemaphore.Release();
            }

            return systemFonts;
        }
        public static Task LoadFontsAsync(bool clearExisting = true)
        {
            // It's possible to go down this path if the font collection
            // was loaded during app pre--launch
            if (clearExisting is false && Fonts is not null)
                return Task.CompletedTask;

            Fonts = null;

            return Task.Run(async () =>
            {
                // Reset meta to false;
                UpdateMeta(null);

                await _loadSemaphore.WaitAsync().ConfigureAwait(false);
                var interop = Ioc.Default.GetService<NativeInterop>();

                // Load SystemFonts and imported fonts in parallel
                IReadOnlyList<StorageFile> files = null;
                Task<List<DWriteFontSet>> setsTask = Task.Run(async () =>
                {
                    files = await _importFolder.GetFilesAsync();
                    return interop.GetFonts(files).ToList();
                });
                Task<DWriteFontSet> init = InitialiseAsync();
                await Task.WhenAll(init, setsTask);

                // Load in System Fonts
                var systemFonts = init.Result;
                Dictionary<string, InstalledFont> resultList = new (systemFonts.Fonts.Count);
                UpdateMeta(systemFonts);

                /* Add imported fonts */
                IReadOnlyList<DWriteFontSet> sets = setsTask.Result;
                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    if (_ignoredFonts.Contains(file.Name))
                        continue;

                    DWriteFontSet importedFonts = sets[i];
                    UpdateMeta(importedFonts);
                    foreach (DWriteFontFace font in importedFonts.Fonts)
                    {
                        AddFont(resultList, font, file);
                    }
                }

                var imports = resultList.ToDictionary(d => d.Key, v => v.Value.Clone());

                /* Add all system fonts */
                foreach (var font in systemFonts.Fonts)
                    AddFont(resultList, font);
                

                /* Order everything appropriately */
                Fonts = CreateFontList(resultList);
                ImportedFonts = CreateFontList(imports);


                if (Fallback == null)
                    Fallback = interop.CreateEmptyFallback();

                _loadSemaphore.Release();

                WeakReferenceMessenger.Default.Send(new FontListCreatedMessage());
            });
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<InstalledFont> CreateFontList(Dictionary<string, InstalledFont> fonts)
        {
            return fonts.OrderBy(f => f.Key).Select(f =>
            {
                f.Value.SortVariants();
                return f.Value;
            }).ToList();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateMeta(DWriteFontSet set)
        {
            if (set == null)
            {
                HasRemoteFonts = HasAppxFonts = HasVariableFonts = false;
                return;
            }

            HasRemoteFonts = HasRemoteFonts || set.CloudFontCount > 0;
            HasAppxFonts = HasAppxFonts || set.AppxFontCount > 0;
            HasVariableFonts = HasVariableFonts || set.VariableFontCount > 0;
        }

        internal static List<FontVariant> GetImportedVariants()
        {
            return Fonts.Where(f => f.HasImportedFiles)
                        .SelectMany(f => f.Variants.Where(v => v.IsImported))
                        .ToList();
        }

        /* 
         * Helper method for adding fonts. 
         */
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddFont(
            IDictionary<string, InstalledFont> fontList,
            DWriteFontFace font,
            StorageFile file = null)
        {
            try
            {
                var familyName = font.Properties.FamilyName;
                if (!string.IsNullOrEmpty(familyName))
                {
                    /* Check if we already have a listing for this fontFamily */
                    if (fontList.TryGetValue(familyName, out var fontFamily))
                    {
                        fontFamily.AddVariant(font, file);
                    }
                    else
                    {
                        fontList[familyName] = new InstalledFont(familyName, font, file);
                    }
                }
            }
            catch (Exception)
            {
                // Corrupted font files throw an exception
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetAppPath(StorageFile file)
        {
            if (file.Path.StartsWith(ApplicationData.Current.TemporaryFolder.Path))
            {
                var str = file.Path.Replace(ApplicationData.Current.TemporaryFolder.Path, "ms-appdata:///temp")
                    .Replace("\\", "/");
                return str;
            }
            var temp = Path.GetDirectoryName(file.Path).EndsWith(TEMP);
            return $"ms-appdata:///local/{(temp ? $"{TEMP}/" :  string.Empty)}{file.Name}";
        }

        internal static Task<FontImportResult> ImportFontsAsync(IReadOnlyList<IStorageItem> items)
        {
            return Task.Run(async () =>
            {
                List<StorageFile> imported = new ();
                List<StorageFile> existing = new ();
                List<(IStorageItem, string)> invalid = new ();

                foreach (var item in items)
                {
                    if (item is not StorageFile file)
                    {
                        invalid.Add((item, Localization.Get("ImportNotAFile")));
                        continue;
                    }

                    if (!file.IsAvailable)
                    {
                        invalid.Add((item, Localization.Get("ImportUnavailableFile")));
                        continue;
                    }

                    if (_ignoredFonts.Contains(file.Name))
                    {
                        invalid.Add((item, Localization.Get("ImportPendingDelete")));
                        continue;
                    }

                    // For WOFF files we can attempt to convert the file to OTF before loading
                    var src = file;
                    var convertResult = await FontConverter.TryConvertAsync(file);
                    if (convertResult.Result is not ConversionStatus.OK)
                    {
                        if (convertResult.Result == ConversionStatus.UnsupportedWOFF2)
                            invalid.Add((src, Localization.Get("ImportWOFF2NotSupported")));
                        else
                            invalid.Add((src, Localization.Get("ImportFailedWoff")));
                        continue;
                    }
                    else
                        file = convertResult.File;

                    if (SupportedFormats.Contains(file.FileType.ToLower()))
                    {
                        // TODO : What do we do if a font with the same file name already exists?

                        /* Explicitly not using StorageFile/Folder API's here because there
                         * are some strange import bugs when checking a file exists */
                        if (!File.Exists(Path.Combine(_importFolder.Path, file.Name)))
                        {
                            StorageFile fontFile;
                            try
                            {
                                /* Copy to local folder. We can only verify font file when it's inside
                                * the App's Local folder due to CanvasFontSet file restrictions */
                                fontFile = await file.CopyAsync(_importFolder);
                            }
                            catch (Exception)
                            {
                                invalid.Add((file, Localization.Get("ImportFileCopyFail")));
                                continue;
                            }

                            /* Avoid Garbage Collection (?) issue preventing immediate file deletion 
                             * by dropping to C++ */
                            if (DirectWrite.HasValidFonts(new Uri(GetAppPath(fontFile))))
                            {
                                imported.Add(fontFile);
                            }
                            else
                            {
                                invalid.Add((file, Localization.Get("ImportUnsupportedFontType")));
                                await fontFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            }
                        }
                        else
                        {
                            existing.Add(file);
                        }
                    }
                    else
                        invalid.Add((file, Localization.Get("ImportUnsupportedFileType")));
                }

                if (imported.Count > 0)
                {
                    await LoadFontsAsync();
                }

                return new FontImportResult(imported, existing, invalid);
            });
        }

        

        /// <summary>
        /// Returns true if all fonts were deleted.
        /// Returns false is some failed - these will be deleted on next start up.
        /// </summary>
        /// <param name="font"></param>
        /// <returns></returns>
        internal static async Task<bool> RemoveFontAsync(InstalledFont font)
        {
            Fonts = null;
            font.PrepareForDelete();

            var variants = font.Variants.Where(v => v.IsImported).ToList();
            var success = true;

            foreach (var variant in variants)
            {
                variant.Dispose();

                GC.Collect(); // required to prevent "File in use" exception

                try
                {
                    if (await _importFolder.TryGetItemAsync(variant.FileName)
                                        is StorageFile file)
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                }
                catch 
                {
                    _ignoredFonts.Add(variant.FileName);
                    success = false;
                }
            }

            /* If we did get a "File In Use" or something similar, 
             * make sure we delete the file during the next start up */
            if (success == false)
            {
                var pending = await ApplicationData.Current.LocalCacheFolder.CreateFileAsync(PENDING, CreationCollisionOption.OpenIfExists);
                await FileIO.WriteLinesAsync(pending, _ignoredFonts);
            }

            await LoadFontsAsync();
            return success;
        }

        private static async Task<StorageFile> TryGetFileAsync(string path)
        {
            try
            {
                return await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private static Task CleanUpPendingDeletesAsync()
        {
            return Task.Run(async() =>
            {
                /* If we fail to delete a font at runtime, delete it now before we load anything */
                var path = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, PENDING);
                var fontPath = ApplicationData.Current.LocalFolder.Path;
                if (File.Exists(path) && await TryGetFileAsync(path).ConfigureAwait(false) is StorageFile file)
                {
                    var lines = await FileIO.ReadLinesAsync(file).AsTask().ConfigureAwait(false);

                    var moreFails = new List<string>();
                    foreach (var line in lines)
                    {
                        if (await TryGetFileAsync(Path.Combine(fontPath, line)).ConfigureAwait(false) is StorageFile deleteFile)
                        {
                            try
                            {
                                await deleteFile.DeleteAsync().AsTask().ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                moreFails.Add(line);
                            }
                        }
                    }

                    if (moreFails.Count > 0)
                        await FileIO.WriteLinesAsync(file, moreFails).AsTask().ConfigureAwait(false);
                    else
                        await file.DeleteAsync().AsTask().ConfigureAwait(false);
                }
            });
            
        }

        private static Task CleanUpTempFolderAsync()
        {
            return Task.Run(async () =>
            {
                var path = Path.Combine(_importFolder.Path, TEMP);
                if (Directory.Exists(path))
                {
                    var folder = await StorageFolder.GetFolderFromPathAsync(path).AsTask().ConfigureAwait(false);
                    foreach (var file in await folder.GetFilesAsync().AsTask().ConfigureAwait(false))
                    {
                        try
                        {
                            await file.DeleteAsync().AsTask().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                        }
                    }
                }
            });
            
        }

        internal static async Task<InstalledFont> LoadFromFileAsync(StorageFile file)
        {
            await InitialiseAsync().ConfigureAwait(false);

            var convert = await FontConverter.TryConvertAsync(file);
            if (convert.Result != ConversionStatus.OK)
                return null;

            file = convert.File;

            var folder = await _importFolder.CreateFolderAsync(TEMP, CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
            var localFile = await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName).AsTask().ConfigureAwait(false);

            var resultList = new Dictionary<string, InstalledFont>();

            var interop = Ioc.Default.GetService<NativeInterop>();
            var fontSet = interop.GetFonts(localFile);

            //var fontSet = DirectWrite.GetFonts(new Uri(GetAppPath(localFile)));
            foreach (var font in fontSet.Fonts)
            {
                AddFont(resultList, font, localFile);
            }

            GC.Collect();
            return resultList.Count > 0 ? resultList.First().Value : null;
        }

        public static Task<FolderContents> LoadToTempFolderAsync(FolderOpenOptions options)
        {
            return Task.Run(async () =>
            {
                var folder = options.Root as StorageFolder;
                Windows.Storage.AccessCache.StorageApplicationPermissions.FutureAccessList.AddOrReplace("PickedFolderToken", folder);

                List<StorageFile> files = new();

                try
                {
                    var query = folder.CreateFileQueryWithOptions(
                        new QueryOptions(CommonFileQuery.DefaultQuery, FontFinder.ImportFormats)
                        {
                            FolderDepth = options.Recursive ? FolderDepth.Deep : FolderDepth.Shallow,
                            IndexerOption = IndexerOption.UseIndexerWhenAvailable,
                        });

                    files = (await query.GetFilesAsync().AsTask(options.Token.Value)).ToList();
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException) { }

                FolderContents contents = await LoadToTempFolderAsync(files, options).ConfigureAwait(false);
                if (options.IsCancelled)
                    return contents;

                contents.UpdateFontSet();
                return contents;
            });
        }

        public static async Task<FolderContents> LoadZipToTempFolderAsync(StorageFile zipFile)
        {
            List<StorageFile> files = new (){ zipFile };
            var contents = await LoadToTempFolderAsync(files, new FolderOpenOptions { AllowZip = true, Root = zipFile }).ConfigureAwait(false);
            contents.UpdateFontSet();
            return contents;
        }

        public static Task<FolderContents> LoadToTempFolderAsync(
            IReadOnlyList<StorageFile> files, FolderOpenOptions options, StorageFolder tempFolder = null, FolderContents contents = null)
        {
            return Task.Run(async () =>
            {
                await InitialiseAsync().ConfigureAwait(false);

                // 1. Create temporary storage folder
                var dest = tempFolder ?? await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("i", CreationCollisionOption.GenerateUniqueName)
                    .AsTask().ConfigureAwait(false);
                contents ??= new(options.Root, dest);
                Func<StorageFile, Task<List<StorageFile>>> func = (storageFile) => GetFontsAsync(storageFile, dest, options);
                if (options.IsCancelled)
                    return contents;

                // 2. Copy all files to temporary storage
                List<Task<List<StorageFile>>> tasks = files
                    .Where(f => ImportFormats.Contains(f.FileType.ToLower()))
                    .Select(func)
                    .ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);

                if (options.IsCancelled)
                    return contents;

                // 3. Create font sets
                var interop = Utils.GetInterop();
                var results = tasks.Where(t => t.Result is not null).SelectMany(t => t.Result).ToList();
                var dwSets = interop.GetFonts(results).ToList();

                // 4. Create InstalledFonts list
                for (int i = 0; i < dwSets.Count; i++)
                {
                    StorageFile file = results[i];
                    DWriteFontSet set = dwSets[i];

                    foreach (DWriteFontFace font in set.Fonts)
                        AddFont(contents.FontCache, font, file);
                }

                return contents;
            });
            
        }

        private static async Task<List<StorageFile>> GetFontsAsync(StorageFile f, StorageFolder dest, FolderOpenOptions options)
        {
            if (options.IsCancelled)
                return new();

            List<StorageFile> results = new();

            if (f.FileType == ".zip")
            {
                if (options.AllowZip)
                    results = await FontConverter.ExtractFontsFromZipAsync(f, dest, options).ConfigureAwait(false);
            }
            else
            {
                var convert = await FontConverter.TryConvertAsync(f, dest).ConfigureAwait(false);
                if (convert.Result is not ConversionStatus.OK || options.IsCancelled)
                    goto Exit;

                if (convert.File == f)
                {
                    var file = await f.CopyAsync(dest, f.Name, NameCollisionOption.GenerateUniqueName).AsTask().ConfigureAwait(false);
                    results.Add(file);
                }
                else
                    results.Add(convert.File);
            }

        Exit:
            options?.Increment(results.Count);
            return results;
        }

        public static bool IsMDL2(FontVariant variant) => variant != null && (
            variant.FamilyName.Contains("MDL2") || variant.FamilyName.Contains("Fluent Icons"));

        public static bool IsSystemSymbolFamily(FontVariant variant) => variant != null && (
            variant.FamilyName.Equals("Segoe MDL2 Assets") || variant.FamilyName.Equals("Segoe Fluent Icons"));
    }
}