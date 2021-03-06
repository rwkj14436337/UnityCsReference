// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using AssetStorePackageInfo = UnityEditor.PackageInfo;

namespace UnityEditor.PackageManager.UI.AssetStore
{
    internal sealed class AssetStoreClient
    {
        static IAssetStoreClient s_Instance = null;
        public static IAssetStoreClient instance => s_Instance ?? AssetStoreClientInternal.instance;

        [Serializable]
        internal class AssetStoreClientInternal : ScriptableSingleton<AssetStoreClientInternal>, IAssetStoreClient, ISerializationCallbackReceiver
        {
            private static readonly string k_AssetStoreDownloadPrefix = "content__";

            public event Action<IEnumerable<IPackage>> onPackagesChanged = delegate {};
            public event Action<DownloadProgress> onDownloadProgress = delegate {};

            public event Action onListOperationStart = delegate {};
            public event Action onListOperationFinish = delegate {};
            public event Action<Error> onOperationError = delegate {};

            public event Action<ProductList, bool> onProductListFetched = delegate {};
            public event Action<long> onProductFetched = delegate {};

            public event Action onFetchDetailsStart = delegate {};
            public event Action onFetchDetailsFinish = delegate {};

            private Dictionary<string, DownloadProgress> m_Downloads = new Dictionary<string, DownloadProgress>();

            private Dictionary<string, PackageState> m_UpdateDetails = new Dictionary<string, PackageState>();

            private HashSet<long> m_PackageDetailsFetched = new HashSet<long>();

            [SerializeField]
            private string[] m_SerializedUpdateDetailKeys = new string[0];

            [SerializeField]
            private PackageState[] m_SerializedUpdateDetailValues = new PackageState[0];

            [SerializeField]
            private DownloadProgress[] m_SerializedDownloads = new DownloadProgress[0];

            [SerializeField]
            private long[] m_SerializedPackageDetailsFetched;

            [SerializeField]
            private bool m_SetupDone;

            public void OnAfterDeserialize()
            {
                m_Downloads.Clear();
                foreach (var p in m_SerializedDownloads)
                {
                    m_Downloads[p.packageId] = p;
                }

                m_UpdateDetails.Clear();
                for (var i = 0; i < m_SerializedUpdateDetailKeys.Length; i++)
                {
                    m_UpdateDetails[m_SerializedUpdateDetailKeys[i]] = m_SerializedUpdateDetailValues[i];
                }

                m_PackageDetailsFetched = new HashSet<long>(m_SerializedPackageDetailsFetched);
            }

            public void OnBeforeSerialize()
            {
                m_SerializedDownloads = m_Downloads.Values.ToArray();

                m_SerializedUpdateDetailKeys = new string[m_UpdateDetails.Count];
                m_SerializedUpdateDetailValues = new PackageState[m_UpdateDetails.Count];
                var i = 0;
                foreach (var kp in m_UpdateDetails)
                {
                    m_SerializedUpdateDetailKeys[i] = kp.Key;
                    m_SerializedUpdateDetailValues[i] = kp.Value;
                    i++;
                }

                m_SerializedPackageDetailsFetched = m_PackageDetailsFetched.ToArray();
            }

            public void Fetch(long productId)
            {
                if (!ApplicationUtil.instance.isUserLoggedIn)
                {
                    onOperationError?.Invoke(new Error(NativeErrorCode.Unknown, L10n.Tr("User not logged in")));
                    return;
                }

                var id = productId.ToString();
                var localPackages = GetLocalPackages();
                if (localPackages.ContainsKey(id))
                    RefreshProductUpdateDetails(new Dictionary<string, AssetStorePackageInfo> { { id, localPackages[id] } }, () => { FetchInternal(localPackages, productId); });
                else
                    FetchInternal(localPackages, productId);
            }

            private void FetchInternal(IDictionary<string, AssetStorePackageInfo> localPackages, long productID)
            {
                // create a placeholder before fetching data from the cloud for the first time
                if (!m_PackageDetailsFetched.Contains(productID))
                {
                    onPackagesChanged?.Invoke(new[] { new PlaceholderPackage(productID.ToString(), PackageTag.AssetStore) });
                }

                FetchDetailsInternal(new[] { productID }, localPackages);

                onProductFetched?.Invoke(productID);
            }

            public void List(int offset, int limit, string searchText = "", bool fetchDetails = true)
            {
                if (!ApplicationUtil.instance.isUserLoggedIn)
                {
                    onOperationError?.Invoke(new Error(NativeErrorCode.Unknown, L10n.Tr("User not logged in")));
                    return;
                }

                onListOperationStart?.Invoke();

                var localPackages = GetLocalPackages();
                if (offset == 0)
                    RefreshProductUpdateDetails(localPackages, () => { ListInternal(localPackages, offset, limit, searchText, fetchDetails); });
                else
                    ListInternal(localPackages, offset, limit, searchText, fetchDetails);
            }

            private void ListInternal(IDictionary<string, AssetStorePackageInfo> localPackages, int offset, int limit, string searchText, bool fetchDetails)
            {
                AssetStoreRestAPI.instance.GetProductIDList(offset, limit, searchText, productList =>
                {
                    if (!productList.isValid)
                    {
                        onListOperationFinish?.Invoke();
                        onOperationError?.Invoke(new Error(NativeErrorCode.Unknown, productList.errorMessage));
                        return;
                    }

                    if (!ApplicationUtil.instance.isUserLoggedIn)
                    {
                        productList.total = 0;
                        productList.list.Clear();
                    }

                    onProductListFetched?.Invoke(productList, fetchDetails);

                    if (productList.list.Count == 0)
                    {
                        onListOperationFinish?.Invoke();
                        return;
                    }

                    var placeholderPackages = new List<IPackage>();

                    foreach (var product in productList.list)
                    {
                        // create a placeholder before fetching data from the cloud for the first time
                        if (!m_PackageDetailsFetched.Contains(product))
                            placeholderPackages.Add(new PlaceholderPackage(product.ToString(), PackageTag.AssetStore));
                    }

                    if (placeholderPackages.Any())
                        onPackagesChanged?.Invoke(placeholderPackages);

                    onListOperationFinish?.Invoke();

                    if (fetchDetails)
                        FetchDetailsInternal(productList.list, localPackages);
                });
            }

            public void FetchDetails(IEnumerable<long> packageIds)
            {
                FetchDetailsInternal(packageIds, GetLocalPackages());
            }

            private void FetchDetailsInternal(IEnumerable<long> packageIds, IDictionary<string, AssetStorePackageInfo> localPackages)
            {
                var countProduct = packageIds.Count();
                if (countProduct == 0)
                    return;

                onFetchDetailsStart?.Invoke();

                foreach (var id in packageIds)
                {
                    AssetStoreRestAPI.instance.GetProductDetail(id, productDetail =>
                    {
                        AssetStorePackage package;
                        object error;
                        if (!productDetail.TryGetValue("errorMessage", out error))
                        {
                            AssetStorePackageInfo localPackage;
                            if (localPackages.TryGetValue(id.ToString(), out localPackage))
                            {
                                productDetail["localPath"] = localPackage.packagePath;
                            }
                            else
                            {
                                productDetail["localPath"] = string.Empty;
                            }

                            package = new AssetStorePackage(id.ToString(), productDetail);
                            if (m_UpdateDetails.ContainsKey(package.uniqueId))
                            {
                                package.SetState(m_UpdateDetails[package.uniqueId]);
                            }

                            if (package.state == PackageState.Outdated && !string.IsNullOrEmpty(localPackage.packagePath))
                            {
                                package.m_FetchedVersion.localPath = string.Empty;

                                try
                                {
                                    var info = new AssetStorePackageVersion.SpecificVersionInfo();
                                    var item = Json.Deserialize(localPackage.jsonInfo) as Dictionary<string, object>;
                                    info.versionId = item["version_id"] as string;
                                    info.versionString = item["version"] as string;
                                    info.publishedDate = item["pubdate"] as string;
                                    info.supportedVersion = item["unity_version"] as string;

                                    var installedVersion = new AssetStorePackageVersion(id.ToString(), productDetail, info);
                                    installedVersion.localPath = localPackage.packagePath;

                                    package.AddVersion(installedVersion);
                                }
                                catch (Exception)
                                {
                                }
                            }
                            m_PackageDetailsFetched.Add(id);
                        }
                        else
                            package = new AssetStorePackage(id.ToString(), new Error(NativeErrorCode.Unknown, error as string));

                        onPackagesChanged?.Invoke(new[] { package });

                        countProduct--;
                        if (countProduct == 0)
                            onFetchDetailsFinish?.Invoke();
                    });
                }
            }

            public void Refresh(IEnumerable<IPackage> packages)
            {
                if (packages == null || !packages.Any() || !ApplicationUtil.instance.isUserLoggedIn)
                    return;

                var localInfos = new Dictionary<string, AssetStorePackageVersion.SpecificVersionInfo>();
                var localPackages = AssetStoreUtils.instance.GetLocalPackageList();
                foreach (var p in localPackages)
                {
                    if (!string.IsNullOrEmpty(p.jsonInfo))
                    {
                        var item = Json.Deserialize(p.jsonInfo) as Dictionary<string, object>;
                        if (item != null && item.ContainsKey("id") && item["id"] is string)
                        {
                            localInfos[(string)item["id"]] = new AssetStorePackageVersion.SpecificVersionInfo
                            {
                                packagePath = p.packagePath,
                                versionString = item.ContainsKey("version") && item["version"] is string? (string)item["version"] : string.Empty,
                                versionId = item.ContainsKey("version_id") && item["version_id"] is string? (string)item["version_id"] : string.Empty,
                                publishedDate = item.ContainsKey("pubdate") && item["pubdate"] is string? (string)item["pubdate"] : string.Empty,
                                supportedVersion = item.ContainsKey("unity_version") && item["unity_version"] is string? (string)item["unity_version"] : string.Empty
                            };
                        }
                    }
                }

                var updatedPackages = new List<IPackage>();
                foreach (var package in packages)
                {
                    var assetStorePackage = package as AssetStorePackage;
                    if (assetStorePackage == null)
                        continue;

                    AssetStorePackageVersion.SpecificVersionInfo localInfo;
                    localInfos.TryGetValue(assetStorePackage.uniqueId, out localInfo);

                    var packageChanged = false;
                    if (localInfo == null)
                    {
                        if (assetStorePackage.installedVersion != null)
                        {
                            assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                            if (assetStorePackage.m_FetchedVersion != assetStorePackage.m_LocalVersion)
                            {
                                assetStorePackage.RemoveVersion(assetStorePackage.m_LocalVersion);
                            }

                            assetStorePackage.SetState(PackageState.UpToDate);
                            packageChanged = true;
                        }
                    }
                    else if (assetStorePackage.installedVersion == null || localInfo.versionString != assetStorePackage.installedVersion.versionString)
                    {
                        if (assetStorePackage.m_FetchedVersion.versionString == localInfo.versionString)
                        {
                            assetStorePackage.m_FetchedVersion.localPath = localInfo.packagePath;
                            if (assetStorePackage.m_LocalVersion != assetStorePackage.m_FetchedVersion)
                            {
                                assetStorePackage.RemoveVersion(assetStorePackage.m_LocalVersion);
                            }

                            assetStorePackage.SetState(PackageState.UpToDate);
                        }
                        else if (assetStorePackage.m_LocalVersion.versionString == localInfo.versionString)
                        {
                            assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                            assetStorePackage.m_LocalVersion.localPath = localInfo.packagePath;
                            assetStorePackage.SetState(PackageState.Outdated);
                        }
                        else
                        {
                            assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                            assetStorePackage.AddVersion(new AssetStorePackageVersion(assetStorePackage.m_FetchedVersion, localInfo));
                            assetStorePackage.m_LocalVersion.localPath = localInfo.packagePath;
                            assetStorePackage.SetState(PackageState.Outdated);
                        }

                        packageChanged = true;
                    }

                    if (packageChanged)
                    {
                        if (m_UpdateDetails.ContainsKey(assetStorePackage.uniqueId))
                            m_UpdateDetails[assetStorePackage.uniqueId] = assetStorePackage.state;

                        updatedPackages.Add(package);
                    }
                }

                if (updatedPackages.Any())
                    onPackagesChanged?.Invoke(updatedPackages);
            }

            public void Refresh(IPackage package)
            {
                if (!ApplicationUtil.instance.isUserLoggedIn)
                    return;

                var assetStorePackage = package as AssetStorePackage;
                if (assetStorePackage == null)
                    return;

                AssetStorePackageVersion.SpecificVersionInfo localInfo = null;
                var localPackage = AssetStoreUtils.instance.GetLocalPackageList().FirstOrDefault(p =>
                {
                    if (!string.IsNullOrEmpty(p.jsonInfo))
                    {
                        var item = Json.Deserialize(p.jsonInfo) as Dictionary<string, object>;
                        if (item != null && item.ContainsKey("id") && item["id"] is string && package.uniqueId == (string)item["id"])
                        {
                            localInfo = new AssetStorePackageVersion.SpecificVersionInfo
                            {
                                versionString = item.ContainsKey("version") && item["version"] is string? (string)item["version"] : string.Empty,
                                versionId = item.ContainsKey("version_id") && item["version_id"] is string? (string)item["version_id"] : string.Empty,
                                publishedDate = item.ContainsKey("pubdate") && item["pubdate"] is string? (string)item["pubdate"] : string.Empty,
                                supportedVersion = item.ContainsKey("unity_version") && item["unity_version"] is string? (string)item["unity_version"] : string.Empty
                            };
                            return true;
                        }
                    }
                    return false;
                });

                var packageChanged = false;
                if (localInfo == null)
                {
                    if (assetStorePackage.installedVersion != null)
                    {
                        assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                        if (assetStorePackage.m_FetchedVersion != assetStorePackage.m_LocalVersion)
                        {
                            assetStorePackage.RemoveVersion(assetStorePackage.m_LocalVersion);
                        }

                        assetStorePackage.SetState(PackageState.UpToDate);
                        packageChanged = true;
                    }
                }
                else if (assetStorePackage.installedVersion == null || localInfo.versionString != assetStorePackage.installedVersion.versionString)
                {
                    if (assetStorePackage.m_FetchedVersion.versionString == localInfo.versionString)
                    {
                        assetStorePackage.m_FetchedVersion.localPath = localPackage.packagePath;
                        if (assetStorePackage.m_LocalVersion != assetStorePackage.m_FetchedVersion)
                        {
                            assetStorePackage.RemoveVersion(assetStorePackage.m_LocalVersion);
                        }
                        assetStorePackage.SetState(PackageState.UpToDate);
                    }
                    else if (assetStorePackage.m_LocalVersion.versionString == localInfo.versionString)
                    {
                        assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                        assetStorePackage.m_LocalVersion.localPath = localPackage.packagePath;
                        assetStorePackage.SetState(PackageState.Outdated);
                    }
                    else
                    {
                        assetStorePackage.m_FetchedVersion.localPath = string.Empty;
                        assetStorePackage.AddVersion(new AssetStorePackageVersion(assetStorePackage.m_FetchedVersion, localInfo));
                        assetStorePackage.m_LocalVersion.localPath = localPackage.packagePath;
                        assetStorePackage.SetState(PackageState.Outdated);
                    }

                    packageChanged = true;
                }

                if (packageChanged)
                {
                    if (m_UpdateDetails.ContainsKey(assetStorePackage.uniqueId))
                        m_UpdateDetails[assetStorePackage.uniqueId] = assetStorePackage.state;
                    onPackagesChanged?.Invoke(new[] { package });
                }
            }

            public bool IsAnyDownloadInProgress()
            {
                return m_Downloads.Values.Any(progress => progress.state == DownloadProgress.State.InProgress || progress.state == DownloadProgress.State.Started);
            }

            private static string AssetStoreCompatibleKey(string packageId)
            {
                if (packageId.StartsWith(k_AssetStoreDownloadPrefix))
                    return packageId;

                return k_AssetStoreDownloadPrefix + packageId;
            }

            public bool IsDownloadInProgress(string packageId)
            {
                DownloadProgress progress;
                if (!GetDownloadProgress(packageId, out progress))
                    return false;

                return progress.state == DownloadProgress.State.InProgress || progress.state == DownloadProgress.State.Started;
            }

            public bool GetDownloadProgress(string packageId, out DownloadProgress progress)
            {
                progress = null;
                return m_Downloads.TryGetValue(AssetStoreCompatibleKey(packageId), out progress);
            }

            public void Download(string packageId)
            {
                DownloadProgress progress;
                if (GetDownloadProgress(packageId, out progress))
                {
                    if (progress.state != DownloadProgress.State.Started &&
                        progress.state != DownloadProgress.State.InProgress &&
                        progress.state != DownloadProgress.State.Decrypting)
                    {
                        m_Downloads.Remove(AssetStoreCompatibleKey(packageId));
                    }
                    else
                    {
                        onDownloadProgress?.Invoke(progress);
                        return;
                    }
                }

                progress = new DownloadProgress(packageId);
                m_Downloads[AssetStoreCompatibleKey(packageId)] = progress;
                onDownloadProgress?.Invoke(progress);

                var id = long.Parse(packageId);
                AssetStoreDownloadOperation.instance.DownloadUnityPackageAsync(id, result =>
                {
                    progress.state = result.downloadState;
                    if (result.downloadState == DownloadProgress.State.Error)
                        progress.message = result.errorMessage;

                    onDownloadProgress?.Invoke(progress);
                });
            }

            public void AbortDownload(string packageId)
            {
                DownloadProgress progress;
                if (!GetDownloadProgress(packageId, out progress))
                    return;

                if (progress.state == DownloadProgress.State.Aborted || progress.state == DownloadProgress.State.Completed || progress.state == DownloadProgress.State.Error)
                    return;

                var id = long.Parse(packageId);
                AssetStoreDownloadOperation.instance.AbortDownloadPackageAsync(id, result =>
                {
                    progress.state = DownloadProgress.State.Aborted;
                    progress.current = progress.total;
                    progress.message = L10n.Tr("Download aborted");

                    onDownloadProgress?.Invoke(progress);

                    m_Downloads.Remove(AssetStoreCompatibleKey(packageId));
                });
            }

            // Used by AssetStoreUtils
            public void OnDownloadProgress(string packageId, string message, ulong bytes, ulong total)
            {
                DownloadProgress progress;
                if (!GetDownloadProgress(packageId, out progress))
                {
                    if (packageId.StartsWith(k_AssetStoreDownloadPrefix))
                        packageId = packageId.Substring(k_AssetStoreDownloadPrefix.Length);
                    progress = new DownloadProgress(packageId) { state = DownloadProgress.State.InProgress, message = "downloading" };
                    m_Downloads[AssetStoreCompatibleKey(packageId)] = progress;
                }

                progress.current = bytes;
                progress.total = total;
                progress.message = message;

                if (message == "ok")
                    progress.state = DownloadProgress.State.Completed;
                else if (message == "connecting")
                    progress.state = DownloadProgress.State.Started;
                else if (message == "downloading")
                    progress.state = DownloadProgress.State.InProgress;
                else if (message == "decrypt")
                    progress.state = DownloadProgress.State.Decrypting;
                else if (message == "aborted")
                    progress.state = DownloadProgress.State.Aborted;
                else
                    progress.state = DownloadProgress.State.Error;

                onDownloadProgress?.Invoke(progress);
            }

            public void Setup()
            {
                System.Diagnostics.Debug.Assert(!m_SetupDone);
                m_SetupDone = true;

                ApplicationUtil.instance.onUserLoginStateChange += OnUserLoginStateChange;
                if (ApplicationUtil.instance.isUserLoggedIn)
                {
                    AssetStoreUtils.instance.RegisterDownloadDelegate(this);
                }
            }

            public void Clear()
            {
                System.Diagnostics.Debug.Assert(m_SetupDone);
                m_SetupDone = false;

                AssetStoreUtils.instance.UnRegisterDownloadDelegate(this);
                ApplicationUtil.instance.onUserLoginStateChange -= OnUserLoginStateChange;
            }

            public void Reset()
            {
                m_UpdateDetails.Clear();
                m_PackageDetailsFetched.Clear();
            }

            private void OnUserLoginStateChange(bool loggedIn)
            {
                if (!loggedIn)
                {
                    AssetStoreUtils.instance.UnRegisterDownloadDelegate(this);
                    AbortAllDownloads();
                }
                else
                {
                    AssetStoreUtils.instance.RegisterDownloadDelegate(this);
                }
            }

            public void AbortAllDownloads()
            {
                var currentDownloads = m_Downloads.Values.Where(v => v.state == DownloadProgress.State.Started || v.state == DownloadProgress.State.InProgress)
                    .Select(v => long.Parse(v.packageId)).ToArray();
                m_Downloads.Clear();

                foreach (var download in currentDownloads)
                    AssetStoreDownloadOperation.instance.AbortDownloadPackageAsync(download);
            }

            private void RefreshProductUpdateDetails(IDictionary<string, AssetStorePackageInfo> localPackages, Action doneCallbackAction)
            {
                var needsUpdateDetail = localPackages.Where(kp => m_UpdateDetails[kp.Key] == PackageState.UpToDate);
                if (!needsUpdateDetail.Any())
                {
                    doneCallbackAction?.Invoke();
                }
                else
                {
                    var list = needsUpdateDetail.Select(kp => kp.Value).ToList();
                    AssetStoreRestAPI.instance.GetProductUpdateDetail(list, updateDetails =>
                    {
                        object error;
                        if (!updateDetails.TryGetValue("errorMessage", out error))
                        {
                            var results = updateDetails["results"] as List<object>;
                            foreach (var item in results)
                            {
                                var updateDetail = item as IDictionary<string, object>;
                                var canUpdate = (updateDetail["can_update"] is long? (long)updateDetail["can_update"] : 0) != 0;
                                m_UpdateDetails[updateDetail["id"] as string] = canUpdate ? PackageState.Outdated : PackageState.UpToDate;
                            }
                        }

                        doneCallbackAction?.Invoke();
                    });
                }
            }

            private IDictionary<string, AssetStorePackageInfo> GetLocalPackages()
            {
                var localPackages = new Dictionary<string, AssetStorePackageInfo>();
                foreach (var package in AssetStoreUtils.instance.GetLocalPackageList())
                {
                    var item = Json.Deserialize(package.jsonInfo) as Dictionary<string, object>;
                    if (item != null && item.ContainsKey("id") && item["id"] is string)
                    {
                        var packageId = (string)item["id"];
                        localPackages[packageId] = package;
                        if (!m_UpdateDetails.ContainsKey(packageId))
                            m_UpdateDetails[packageId] = PackageState.UpToDate;
                    }
                }
                return localPackages;
            }
        }
    }
}
