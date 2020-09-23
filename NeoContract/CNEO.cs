using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using Helper = Neo.SmartContract.Framework.Helper;
using System.ComponentModel;
using System.Numerics;
using System;

namespace CNEO
{
    public class CNEO : SmartContract
    {
        private static readonly byte[] GasClaim = "AQzRMe3zyGS8W177xLJfewRRQZY2kddMun".ToScriptHash();

        [DisplayName("transfer")]
        public static event deleTransfer Transferred;
        public delegate void deleTransfer(byte[] from, byte[] to, BigInteger value);

        [DisplayName("refund")]
        public static event deleRefundTarget Refunded;
        public delegate void deleRefundTarget(byte[] txId, byte[] who);

        private static readonly byte[] AssetId = Helper.HexToBytes("9b7cffdaa674beae0f930ebe6085af9093e5fe56b34a5c220ccdcf6efc336fc5"); //NEO Asset ID, littleEndian

        //StorageMap contract, key: "totalSupply"
        //StorageMap refund, key: txHash
        //StorageMap asset, key: account
        //StorageMap txInfo, key: txHash

        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Verification)
            {
                var tx = ExecutionEngine.ScriptContainer as Transaction;
                var inputs = tx.GetInputs();
                var outputs = tx.GetOutputs();
                if ((byte)tx.Type == 0x02)
                {
                    return inputs == null ? Runtime.CheckWitness(GasClaim) : false;
                }
                //Check if the input has been marked
                foreach (var input in inputs)
                {
                    if (input.PrevIndex == 0)//If UTXO n is 0, it is possible to be a marker UTXO
                    {
                        StorageMap refund = Storage.CurrentContext.CreateMap(nameof(refund));
                        var refundMan = refund.Get(input.PrevHash); //0.1
                        //If the input that is marked for refund
                        if (refundMan.Length > 0)
                        {
                            //Only one input and one output is allowed in refund
                            if (inputs.Length != 1 || outputs.Length != 1)
                                return false;
                            return outputs[0].ScriptHash.AsBigInteger() == refundMan.AsBigInteger();
                        }
                    }
                }
                var currentHash = ExecutionEngine.ExecutingScriptHash;
                //If all the inputs are not marked for refund
                BigInteger inputAmount = 0;
                foreach (var refe in tx.GetReferences())
                {
                    if (refe.AssetId.AsBigInteger() != AssetId.AsBigInteger())
                        return false;//Not allowed to operate assets other than NEO

                    if (refe.ScriptHash.AsBigInteger() == currentHash.AsBigInteger())
                        inputAmount += refe.Value;
                }
                //Check that there is no money left this contract
                BigInteger outputAmount = 0;
                foreach (var output in outputs)
                {
                    if (output.ScriptHash.AsBigInteger() == currentHash.AsBigInteger())
                    {
                        outputAmount += output.Value;
                    }                        
                }
                return outputAmount == inputAmount;
            }
            else if (Runtime.Trigger == TriggerType.Application)
            {
                var callscript = ExecutionEngine.CallingScriptHash;

                if (method == "balanceOf") return BalanceOf((byte[])args[0]);

                if (method == "decimals") return Decimals();

                if (method == "getRefundTarget") return GetRefundTarget((byte[])args[0]);

                if (method == "getTxInfo") return GetTxInfo((byte[])args[0]);

                if (method == "mintTokens") return MintTokens();

                if (method == "name") return Name();

                if (method == "refund") return Refund((byte[])args[0]);

                if (method == "symbol") return Symbol();

                if (method == "supportedStandards") return SupportedStandards();

                if (method == "totalSupply") return TotalSupply();

                if (method == "transfer") return Transfer((byte[])args[0], (byte[])args[1], (BigInteger)args[2], callscript);
            }
            else if (Runtime.Trigger == TriggerType.VerificationR) //Backward compatibility, refusing to accept other assets
            {
                var currentHash = ExecutionEngine.ExecutingScriptHash;
                var tx = ExecutionEngine.ScriptContainer as Transaction;
                foreach (var output in tx.GetOutputs())
                {
                    if (output.ScriptHash == currentHash && output.AssetId.AsBigInteger() != AssetId.AsBigInteger())
                        return false;
                }
                return true;
            }
            return false;
        }

        [DisplayName("balanceOf")]
        public static BigInteger BalanceOf(byte[] account)
        {
            if (account.Length != 20)
                throw new InvalidOperationException("The parameter account SHOULD be 20-byte addresses.");
            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            return asset.Get(account).AsBigInteger(); //0.1
        }
        [DisplayName("decimals")]
        public static byte Decimals() => 8;

        [DisplayName("getRefundTarget")]
        public static byte[] GetRefundTarget(byte[] txId)
        {
            if (txId.Length != 32)
                throw new InvalidOperationException("The parameter txId SHOULD be 32-byte transaction hash.");
            StorageMap refund = Storage.CurrentContext.CreateMap(nameof(refund));
            return refund.Get(txId); //0.1
        }

        [DisplayName("getTxInfo")]
        public static TransferInfo GetTxInfo(byte[] txId)
        {
            if (txId.Length != 32)
                throw new InvalidOperationException("The parameter txId SHOULD be 32-byte transaction hash.");
            StorageMap txInfo = Storage.CurrentContext.CreateMap(nameof(txInfo));
            var result = txInfo.Get(txId); //0.1
            if (result.Length == 0) return null;
            return Helper.Deserialize(result) as TransferInfo;
        }

        private static bool IsPayable(byte[] to)
        {
            var c = Blockchain.GetContract(to); //0.1
            return c == null || c.IsPayable;
        }

        /// <summary>
        /// Global Asset -> NEP5 Asset
        /// </summary>
        [DisplayName("mintTokens")]
        public static bool MintTokens()
        {
            var tx = ExecutionEngine.ScriptContainer as Transaction;

            //Person who sends a global asset, receives a NEP5 asset
            byte[] sender = null;
            var inputs = tx.GetReferences();
            foreach (var input in inputs)
            {
                if (input.AssetId.AsBigInteger() == AssetId.AsBigInteger())
                    sender = sender ?? input.ScriptHash;
                //CNEO address as inputs is not allowed
                if (input.ScriptHash.AsBigInteger() == ExecutionEngine.ExecutingScriptHash.AsBigInteger())
                    return false;
            }
            if (GetTxInfo(tx.Hash) != null)
                return false;

            //Amount of exchange
            var outputs = tx.GetOutputs();
            ulong value = 0;
            foreach (var output in outputs)
            {
                if (output.ScriptHash == ExecutionEngine.ExecutingScriptHash &&
                    output.AssetId.AsBigInteger() == AssetId.AsBigInteger())
                {
                    value += (ulong)output.Value;
                }
            }

            //Increase the total amount of contract assets
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            var totalSupply = contract.Get("totalSupply").AsBigInteger(); //0.1
            totalSupply += value;
            contract.Put("totalSupply", totalSupply); //1

            //Issue NEP-5 asset
            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            var amount = asset.Get(sender).AsBigInteger(); //0.1
            asset.Put(sender, amount + value); //1

            SetTxInfo(null, sender, value);
            Transferred(null, sender, value);
            return true;
        }

        [DisplayName("name")]
        public static string Name() => "NEP5 NEO";

        /// <summary>
        /// NEP5 Asset -> Global Asset
        /// In the pre-refund phase you need to build a TX (SC -> SC) whose Input is a UTXO of the contract. 
        /// </summary>
        [DisplayName("refund")]
        public static bool Refund(byte[] from)
        {
            if (from.Length != 20)
                throw new InvalidOperationException("The parameter from SHOULD be 20-byte addresses.");
            var tx = ExecutionEngine.ScriptContainer as Transaction;
            //output[0] Is the asset that the user want to refund
            var preRefund = tx.GetOutputs()[0];
            //refund assets wrong, failed
            if (preRefund.AssetId.AsBigInteger() != AssetId.AsBigInteger()) return false;

            //Not to itself, failed
            if (preRefund.ScriptHash.AsBigInteger() != ExecutionEngine.ExecutingScriptHash.AsBigInteger()) return false;

            //double refund
            StorageMap refund = Storage.CurrentContext.CreateMap(nameof(refund));
            if (refund.Get(tx.Hash).Length > 0) return false; //0.1

            if (!Runtime.CheckWitness(from)) return false; //0.2

            //Reduce the balance of the refund person
            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            var fromAmount = asset.Get(from).AsBigInteger(); //0.1
            var preRefundValue = preRefund.Value;
            if (fromAmount < preRefundValue)
                return false;
            else if (fromAmount == preRefundValue)
                asset.Delete(from); //0.1
            else
                asset.Put(from, fromAmount - preRefundValue); //1
            refund.Put(tx.Hash, from); //1

            //Change the totalSupply
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            var totalSupply = contract.Get("totalSupply").AsBigInteger(); //0.1
            totalSupply -= preRefundValue;
            contract.Put("totalSupply", totalSupply); //1

            SetTxInfo(from, null, preRefundValue);
            Transferred(from, null, preRefundValue);
            Refunded(tx.Hash, from);
            return true;
        }

        private static void SetTxInfo(byte[] from, byte[] to, BigInteger value)
        {
            var txid = (ExecutionEngine.ScriptContainer as Transaction).Hash;
            TransferInfo info = new TransferInfo
            {
                from = from,
                to = to,
                value = value
            };
            StorageMap txInfo = Storage.CurrentContext.CreateMap(nameof(txInfo));
            txInfo.Put(txid, Helper.Serialize(info)); //1
        }

        [DisplayName("symbol")]
        public static string Symbol() => "CNEO";

        [DisplayName("supportedStandards")]
        public static string SupportedStandards() => "{\"NEP-5\", \"NEP-7\", \"NEP-10\"}";

        [DisplayName("totalSupply")]
        public static BigInteger TotalSupply()
        {
            StorageMap contract = Storage.CurrentContext.CreateMap(nameof(contract));
            return contract.Get("totalSupply").AsBigInteger(); //0.1
        }
#if DEBUG
        [DisplayName("transfer")] //Only for ABI file
        public static bool Transfer(byte[] from, byte[] to, BigInteger amount) => true;
#endif
        //Methods of actual execution
        private static bool Transfer(byte[] from, byte[] to, BigInteger amount, byte[] callscript)
        {
            //Check parameters
            if (from.Length != 20 || to.Length != 20)
                throw new InvalidOperationException("The parameters from and to SHOULD be 20-byte addresses.");
            if (amount <= 0)
                throw new InvalidOperationException("The parameter amount MUST be greater than 0.");
            if (!IsPayable(to))
                return false;
            if (!Runtime.CheckWitness(from) && from.AsBigInteger() != callscript.AsBigInteger()) /*0.2*/
                return false;
            StorageMap asset = Storage.CurrentContext.CreateMap(nameof(asset));
            var fromAmount = asset.Get(from).AsBigInteger(); //0.1
            if (fromAmount < amount)
                return false;
            if (from == to)
                return true;

            //Reduce payer balances
            if (fromAmount == amount)
                asset.Delete(from); //0.1
            else
                asset.Put(from, fromAmount - amount); //1

            //Increase the payee balance
            var toAmount = asset.Get(to).AsBigInteger(); //0.1
            asset.Put(to, toAmount + amount); //1
            
            SetTxInfo(from, to, amount);
            Transferred(from, to, amount);
            return true;
        }
    }
}