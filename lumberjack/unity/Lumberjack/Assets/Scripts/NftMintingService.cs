using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using CandyMachineV2;
using CandyMachineV2.Program;
using Frictionless;
using Solana.Unity.Metaplex.NFT.Library;
using Solana.Unity.Programs;
using Solana.Unity.Rpc.Builders;
using Solana.Unity.Rpc.Core.Http;
using Solana.Unity.Rpc.Messages;
using Solana.Unity.Rpc.Types;
using Solana.Unity.SDK;
using Solana.Unity.Wallet;
using SolPlay.DeeplinksNftExample.Utils;
using UnityEngine;
using Creator = Solana.Unity.Metaplex.NFT.Library.Creator;
using MetadataProgram = Solana.Unity.Metaplex.NFT.Library.MetadataProgram;
using PublicKey = Solana.Unity.Wallet.PublicKey;
using Transaction = Solana.Unity.Rpc.Models.Transaction;

namespace SolPlay.Scripts.Services
{
    public class NftMintingService : MonoBehaviour, IMultiSceneSingleton
    {
        public void Awake()
        {
            if (ServiceFactory.Resolve<NftMintingService>() != null)
            {
                Destroy(gameObject);
                return;
            }

            ServiceFactory.RegisterSingleton(this);
        }

        public IEnumerator HandleNewSceneLoaded()
        {
            yield return null;
        }

        public async Task<string> MintNFTFromCandyMachineV2(PublicKey candyMachineKey)
        {
            var account = Web3.Account;

            Account mint = new Account();

            PublicKey associatedTokenAccount =
                AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(account, mint.PublicKey);

            var candyMachineClient = new CandyMachineClient(Web3.Rpc, null);
            var candyMachineWrap = await candyMachineClient.GetCandyMachineAsync(candyMachineKey);
            var candyMachine = candyMachineWrap.ParsedResult;

            var (candyMachineCreator, creatorBump) = CandyMachineUtils.getCandyMachineCreator(candyMachineKey);

            MintNftAccounts mintNftAccounts = new MintNftAccounts
            {
                CandyMachine = candyMachineKey,
                CandyMachineCreator = candyMachineCreator,
                Clock = SysVars.ClockKey,
                InstructionSysvarAccount = CandyMachineUtils.instructionSysVarAccount,
                MasterEdition = CandyMachineUtils.getMasterEdition(mint.PublicKey),
                Metadata = CandyMachineUtils.getMetadata(mint.PublicKey),
                Mint = mint.PublicKey,
                MintAuthority = account,
                Payer = account,
                RecentBlockhashes = SysVars.RecentBlockHashesKey,
                Rent = SysVars.RentKey,
                SystemProgram = SystemProgram.ProgramIdKey,
                TokenMetadataProgram = CandyMachineUtils.TokenMetadataProgramId,
                TokenProgram = TokenProgram.ProgramIdKey,
                UpdateAuthority = account,
                Wallet = candyMachine.Wallet
            };

            var candyMachineInstruction = CandyMachineProgram.MintNft(mintNftAccounts, creatorBump);

            var blockHash = await Web3.Rpc.GetRecentBlockHashAsync();
            var minimumRent =
                await Web3.Rpc.GetMinimumBalanceForRentExemptionAsync(
                    TokenProgram.MintAccountDataSize);

            var serializedTransaction = new TransactionBuilder()
                .SetRecentBlockHash(blockHash.Result.Value.Blockhash)
                .SetFeePayer(account)
                .AddInstruction(
                    SystemProgram.CreateAccount(
                        account,
                        mint.PublicKey,
                        minimumRent.Result,
                        TokenProgram.MintAccountDataSize,
                        TokenProgram.ProgramIdKey))
                .AddInstruction(
                    TokenProgram.InitializeMint(
                        mint.PublicKey,
                        0,
                        account,
                        account))
                .AddInstruction(
                    AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(
                        account,
                        account,
                        mint.PublicKey))
                .AddInstruction(
                    TokenProgram.MintTo(
                        mint.PublicKey,
                        associatedTokenAccount,
                        1,
                        account))
                .AddInstruction(candyMachineInstruction)
                .Build(new List<Account>()
                {
                    account,
                    mint
                });
            
            Transaction deserializedTransaction = Transaction.Deserialize(serializedTransaction);

            Debug.Log($"mint transaction length {serializedTransaction.Length}");
            
            var transactionSignature = await Web3.Wallet.SignAndSendTransaction(deserializedTransaction, commitment: Commitment.Confirmed);

            if (!transactionSignature.WasSuccessful)
            {
                Debug.Log("Mint was not successfull: " + transactionSignature.Reason);
            }
            else
            {
                Debug.Log("Mint Successfull! Woop woop!");
            }

            Debug.Log(transactionSignature.Reason);
            return transactionSignature.Result;
        }
        
        public async Task<string> MintNftWithMetaData(string metaDataUri, string name, string symbol, Action<bool> mintDone = null)
        {
            var account = Web3.Account;
            var rpcClient = Web3.Rpc;

            Account mint = new Account();
            var associatedTokenAccount = AssociatedTokenAccountProgram
                .DeriveAssociatedTokenAccount(account, mint.PublicKey);
            
            var fromAccount = account;

            RequestResult<ResponseValue<ulong>> balance =
                await rpcClient.GetBalanceAsync(account.PublicKey, Commitment.Confirmed);

            if (balance.Result != null && balance.Result.Value < SolanaUtils.SolToLamports / 10)
            {
                Debug.Log("Sol balance is low. Minting may fail");
            }

            Debug.Log($"Balance: {balance.Result.Value} ");
            Debug.Log($"Mint key : {mint.PublicKey} ");

            var blockHash = await rpcClient.GetRecentBlockHashAsync();
            var rentMint = await rpcClient.GetMinimumBalanceForRentExemptionAsync(
                TokenProgram.MintAccountDataSize,
                Commitment.Confirmed
            );
            var rentToken = await rpcClient.GetMinimumBalanceForRentExemptionAsync(
                TokenProgram.TokenAccountDataSize,
                Commitment.Confirmed
            );


            //2. create a mint and a token
            var createMintAccount = SystemProgram.CreateAccount(
                fromAccount,
                mint,
                rentMint.Result,
                TokenProgram.MintAccountDataSize,
                TokenProgram.ProgramIdKey
            );
            var initializeMint = TokenProgram.InitializeMint(
                mint.PublicKey,
                0,
                fromAccount.PublicKey,
                fromAccount.PublicKey
            );
            var createTokenAccount = AssociatedTokenAccountProgram.CreateAssociatedTokenAccount(
                fromAccount,
                fromAccount,
                mint.PublicKey);

            var mintTo = TokenProgram.MintTo(
                mint.PublicKey,
                associatedTokenAccount,
                1,
                fromAccount.PublicKey
            );

            // If you freeze the account the users will not be able to transfer the NFTs anywhere or burn them
            /*var freezeAccount = TokenProgram.FreezeAccount(
                tokenAccount,
                mintAccount,
                fromAccount,
                TokenProgram.ProgramIdKey
            );*/

            // PDA Metadata
            PublicKey metadataAddressPDA;
            byte nonce;
            PublicKey.TryFindProgramAddress(
                new List<byte[]>()
                {
                    Encoding.UTF8.GetBytes("metadata"),
                    MetadataProgram.ProgramIdKey,
                    mint.PublicKey
                },
                MetadataProgram.ProgramIdKey,
                out metadataAddressPDA,
                out nonce
            );

            Console.WriteLine($"PDA METADATA: {metadataAddressPDA}");

            // PDA master edition (Makes sure there can only be one minted) 
            PublicKey masterEditionAddress;

            PublicKey.TryFindProgramAddress(
                new List<byte[]>()
                {
                    Encoding.UTF8.GetBytes("metadata"),
                    MetadataProgram.ProgramIdKey,
                    mint.PublicKey,
                    Encoding.UTF8.GetBytes("edition"),
                },
                MetadataProgram.ProgramIdKey,
                out masterEditionAddress,
                out nonce
            );
            Console.WriteLine($"PDA MASTER: {masterEditionAddress}");

            // Craetors
            var creator1 = new Creator(fromAccount.PublicKey, 100, false);

            // Meta Data
            var data = new Metadata()
            {
                name = name,
                symbol = symbol,
                uri = metaDataUri,
                creators = new List<Creator>() {creator1},
                sellerFeeBasisPoints = 77
            };

            var signers = new List<Account> {fromAccount, mint};
            var transactionBuilder = new TransactionBuilder()
                .SetRecentBlockHash(blockHash.Result.Value.Blockhash)
                .SetFeePayer(fromAccount)
                .AddInstruction(createMintAccount)
                .AddInstruction(initializeMint)
                .AddInstruction(createTokenAccount)
                .AddInstruction(mintTo)
                //.AddInstruction(freezeAccount)
                .AddInstruction(
                    MetadataProgram.CreateMetadataAccount(
                        metadataAddressPDA, // PDA
                        mint,
                        fromAccount.PublicKey,
                        fromAccount.PublicKey,
                        fromAccount.PublicKey, // update Authority 
                        data, // DATA
                        TokenStandard.NonFungible,
                        true,
                        true, // ISMUTABLE,
                        masterEditionKey: null,
                        1,
                        0UL,
                        MetadataVersion.V3
                    )
                )
                .AddInstruction(
                    MetadataProgram.SignMetadata(
                        metadataAddressPDA,
                        creator1.key
                    )
                )
               .AddInstruction(
                    MetadataProgram.PuffMetada(
                        metadataAddressPDA
                    )
                )
                /*.AddInstruction(
                    MetadataProgram.CreateMasterEdition(
                        1,
                        masterEditionAddress,
                        mintAccount,
                        fromAccount.PublicKey,
                        fromAccount.PublicKey,
                        fromAccount.PublicKey,
                        metadataAddressPDA
                    )
                )*/;

            var tx = Transaction.Deserialize(transactionBuilder.Build(new List<Account> {fromAccount, mint}));
            var res = await Web3.Wallet.SignAndSendTransaction(tx, true, Commitment.Confirmed);
            Debug.Log(res.Result);

            if (!res.WasSuccessful)
            {
                mintDone?.Invoke(false);
                Debug
                    .Log("Mint was not successfull: " + res.Reason);
            }
            else
            {
                Debug.Log("Mint Successfull! Woop woop!");
            }

            return res.Result;
        }
    }
}