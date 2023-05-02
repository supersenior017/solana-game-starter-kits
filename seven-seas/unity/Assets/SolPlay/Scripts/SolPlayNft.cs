using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Frictionless;
using Solana.Unity.Rpc;
using Solana.Unity.Rpc.Models;
using Solana.Unity.SDK;
using Solana.Unity.SDK.Nft;
using Solana.Unity.Wallet;
using Solana.Unity.Wallet.Utilities;
using Solnet.Metaplex;
using SolPlay.Scripts.Services;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolPlay.Scripts
{
    [Serializable]
    public class NftImage : iNftFile<Texture2D>
    {
        public string name { get; set; }
        public string extension { get; set; }
        public string externalUrl { get; set; }
        public Texture2D file { get; set; }
    }

    [Serializable]
    public class IgnoreTokenList
    {
        public List<string> TokenList = new List<string>();
    }

    [Serializable]
    public class SolPlayNft
    {
        public Metaplex MetaplexData;
        public AccountInfo AccountInfo;
        public TokenAccount TokenAccount;

        [NonSerialized] public Task LoadingImageTask;
        [NonSerialized] public string LoadingError;

        private static string ImagePrefix = "V2_Image_";
        private static string JsonPrefix = "V2_Json_";

        public SolPlayNft()
        {
        }

        public SolPlayNft(TokenAccount tokenAccount)
        {
            TokenAccount = tokenAccount;
        }

        public SolPlayNft(Metaplex metaplexData)
        {
            MetaplexData = metaplexData;
        }

        public static async Task<NFTProData> TryGetNftPro(string mint, IRpcClient connection)
        {
            AccountInfo accountInfo = await Nft.GetAccountData(mint, connection);

            Debug.Log(Newtonsoft.Json.JsonConvert.SerializeObject(accountInfo));

            if (accountInfo != null && accountInfo.Data != null && accountInfo.Data.Count > 0)
            {
                AccountLayout accountlayout = AccountLayout.DeserializeAccountLayout(accountInfo.Data[0]);
                Debug.Log(Newtonsoft.Json.JsonConvert.SerializeObject(accountlayout));
            }

            return null;
        }

        public async UniTask LoadData(string mint, IRpcClient connection)
        {
            var seeds = new List<byte[]>();
            seeds.Add(Encoding.UTF8.GetBytes("metadata"));
            seeds.Add(new PublicKey("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s").KeyBytes);
            seeds.Add(new PublicKey(mint).KeyBytes);

            if (!PublicKey.TryFindProgramAddress(
                    seeds,
                    new PublicKey("metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s"),
                    out PublicKey metaplexDataPubKey, out var _bump))
            {
                return;
            }

            AccountInfo accountInfo = await Nft.GetAccountData(metaplexDataPubKey.Key, connection);

            if (accountInfo != null && accountInfo.Data != null && accountInfo.Data.Count > 0)
            {
                try
                {
                    Metaplex metaPlex = new Metaplex().ParseData(accountInfo.Data[0]);
                    MetaplexData = metaPlex;
                    AccountInfo = accountInfo;
                    await LoadJson(metaPlex.mint);
                    LoadingImageTask = LoadImage(mint, this);
                                        
                    if (MetaplexData != null)
                    {
                        var nftService = ServiceFactory.Resolve<NftService>();
                        nftService.MetaPlexNFts.Add(this);
                        MessageRouter.RaiseMessage(new NftJsonLoadedMessage(this));
                    }

                }
                catch (Exception e)
                {
                    ServiceFactory.Resolve<NftService>().AddToIgnoredTokenListAndSave(mint);
                    Debug.LogWarning($"Nft data could not be parsed: {e} -> Added to ignore list");
                }
            }
            else
            {
                // Only ignore the nft if account data was actually loaded  
                if (accountInfo != null)
                {
                    ServiceFactory.Resolve<NftService>().AddToIgnoredTokenListAndSave(mint);
                    Debug.LogWarning($"Token seems to not be an NFT -> Added to ignore list");
                }
                else
                {
                    Debug.LogWarning($"Could not load token account for {metaplexDataPubKey.Key}");
                }
            }
        }

        private async UniTask LoadJson(string mint)
        {
            MetaplexJsonData jsonData = await SolPlayFileLoader.LoadFile<MetaplexJsonData>(MetaplexData.data.url);

            if (jsonData != null)
            {
                MetaplexData.data.json = jsonData;
                SolPlayFileLoader.SaveToPersistenDataPath(
                    Path.Combine(Application.persistentDataPath, $"{JsonPrefix}{mint}.json"),
                    this);
            }
            else
            {
                LoadingError = "Could not load jsonData";
            }
        }

        private static async Task LoadImage(string mint, SolPlayNft nft)
        {
            if (nft.MetaplexData.data.json == null)
            {
                return;
            }
            Texture2D texture = await SolPlayFileLoader.LoadFile<Texture2D>(nft.MetaplexData.data.json.image);
            var nftImageSize = ServiceFactory.Resolve<NftService>().NftImageSize;

            Texture2D compressedTexture = Resize(texture, nftImageSize, nftImageSize);

            if (compressedTexture)
            {
                NftImage nftImage = new NftImage();
                nftImage.externalUrl = nft.MetaplexData.data.json.image;
                nftImage.file = compressedTexture;
                nft.MetaplexData.nftImage = nftImage;
                SolPlayFileLoader.SaveToPersistenDataPath(
                    Path.Combine(Application.persistentDataPath, $"{ImagePrefix}{mint}.png"), compressedTexture);
            }
            else
            {
                nft.LoadingError = "Could not load image";
            }

            MessageRouter.RaiseMessage(new NftImageLoadedMessage(nft));
        }

        public static SolPlayNft TryLoadNftFromLocal(string mint)
        {
            SolPlayNft local = SolPlayFileLoader.LoadFileFromLocalPath<SolPlayNft>(
                $"{Path.Combine(Application.persistentDataPath, $"{JsonPrefix}{mint}")}.json");

            if (local != null)
            {
                Texture2D tex =
                    SolPlayFileLoader.LoadFileFromLocalPath<Texture2D>(
                        $"{Path.Combine(Application.persistentDataPath, $"{ImagePrefix}{mint}")}.png");
                if (tex)
                {
                    local.MetaplexData.nftImage = new NftImage();
                    local.MetaplexData.nftImage.file = tex;
                }
                else
                {
                    return null;
                }
            }

            return local;
        }

        /// <summary>
        /// Returns public key of nft
        /// </summary>
        /// <param name="seed"></param>
        /// <param name="programId"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public static PublicKey CreateAddress(List<byte[]> seed, string programId)
        {
            List<byte> buffer = new List<byte>();

            foreach (byte[] item in seed)
            {
                if (item.Length > 32)
                {
                    throw new Exception("Too long");
                }

                buffer.AddRange(item);
            }

            buffer.AddRange(seed[1]);
            byte[] derive = Encoding.UTF8.GetBytes("ProgramDerivedAddress");
            buffer.AddRange(derive);

            SHA256 sha256 = SHA256.Create();
            byte[] hash1 = sha256.ComputeHash(buffer.ToArray());

            if (!hash1.IsOnCurve())
            {
                throw new Exception("Not on curve");
            }

            PublicKey publicKey = new PublicKey(hash1);
            return publicKey;
        }

        /// <summary>
        /// Returns metaplex data pubkey from mint pubkey and programId
        /// </summary>
        /// <param name="mintPublicKey"></param>
        /// <param name="programId"></param>
        /// <returns></returns>
        public static PublicKey FindProgramAddress(string mintPublicKey,
            string programId = "metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s")
        {
            List<byte[]> seeds = new List<byte[]>();

            int nonce = 255;
            seeds.Add(Encoding.UTF8.GetBytes("metadata"));
            seeds.Add(new PublicKey(programId).KeyBytes);
            seeds.Add(new PublicKey(mintPublicKey).KeyBytes);
            seeds.Add(new[] {(byte) nonce});

            PublicKey publicKey = null;

            while (nonce != 0)
            {
                try
                {
                    seeds[3] = new[] {(byte) nonce};
                    publicKey = CreateAddress(seeds, programId);
                    return publicKey;
                }
                catch
                {
                    nonce--;
                    continue;
                }
            }

            return publicKey;
        }

        /// <summary>
        /// Returns metaplex json data from forwarded jsonUrl
        /// </summary>
        public static async Task<T> GetMetaplexJsonData<T>(string jsonUrl)
        {
            HttpClient client = new HttpClient();

            HttpResponseMessage response = await client.GetAsync(jsonUrl);
            response.EnsureSuccessStatusCode();

            try
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                Debug.Log(responseBody);
                T data = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(responseBody);
                client.Dispose();
                return data;
            }
            catch
            {
                client.Dispose();
                return default;
                throw;
            }
        }

        /// <summary>
        /// Resize great textures to small, because of performance
        /// </summary>
        public static Texture2D Resize(Texture2D texture2D, int targetX, int targetY)
        {
            RenderTexture rt = new RenderTexture(targetX, targetY, 24);
            RenderTexture.active = rt;
            Graphics.Blit(texture2D, rt);
            Texture2D result = new Texture2D(targetX, targetY);
            result.ReadPixels(new Rect(0, 0, targetX, targetY), 0, 0);
            result.Apply();
            Object.Destroy(rt);
            return result;
        }
    }
}