/*
    Copyright(c) 2018 Gluwa, Inc.

    This file is part of Creditcoin.

    Creditcoin is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.
    
    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
    GNU Lesser General Public License for more details.
    
    You should have received a copy of the GNU Lesser General Public License
    along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
*/

using ccplugin;
using Microsoft.Extensions.Configuration;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Numerics;

namespace cerc20
{
    public class Erc20 : ICCClientPlugin
    {
        private const string name = "erc20";

        private const string STATE = "s";
        private const string NEW = "n";
        private const string FOUNDED = "f";
        private const string SOURCE_LEDGER = "sl";
        private const string SOURCE_ID = "si";
        private const string SOURCE_AMOUNT = "sa";
        private const string DESTINATION_LEDGER = "dl";
        private const string DESTINATION_ID = "di";
        private const string DESTINATION_AMOUNT = "da";

        private const string ERROR = "error";
        private const string DATA = "data";
        private const string MESSAGE = "message";

        private string erc20TransferAbi = "[{\"constant\":true,\"inputs\":[],\"name\":\"getErc20\",\"outputs\":[{\"internalType\":\"address\",\"name\":\"erc20\",\"type\":\"address\"}],\"payable\":false,\"stateMutability\":\"view\",\"type\":\"function\"},{\"constant\":false,\"inputs\":[{\"internalType\":\"address\",\"name\":\"to\",\"type\":\"address\"},{\"internalType\":\"uint256\",\"name\":\"value\",\"type\":\"uint256\"},{\"internalType\":\"string\",\"name\":\"ccid\",\"type\":\"string\"}],\"name\":\"transfer\",\"outputs\":[{\"internalType\":\"bool\",\"name\":\"success\",\"type\":\"bool\"}],\"payable\":false,\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";
        private string erc20Abi = "[{\"constant\":false,\"inputs\":[{\"internalType\":\"address\",\"name\":\"spender\",\"type\":\"address\"},{\"internalType\":\"uint256\",\"name\":\"value\",\"type\":\"uint256\"}],\"name\":\"approve\",\"outputs\":[{\"internalType\":\"bool\",\"name\":\"success\",\"type\":\"bool\"}],\"payable\":false,\"stateMutability\":\"nonpayable\",\"type\":\"function\"}]";

        private int mConfirmationsExpected = 12;

        public bool Run(bool txid, IConfiguration cfg, string secretOverride, HttpClient httpClient, ITxBuilder txBuilder, ref string progressToken, string url, string[] command, out bool inProgress, out string msg, out string link)
        {
            link = null;

            Debug.Assert(command != null);
            if (command.Length < 2)
            {
                inProgress = false;
                msg = "invalid parameter count";
                return false;
            }

            if (command[0].Equals("registerTransfer", StringComparison.OrdinalIgnoreCase))
            {
                // erc20 RegisterTransfer gain orderId

                inProgress = false;

                if (command.Length != 3)
                {
                    msg = "invalid parameter count";
                    return false;
                }

                string gainString = command[1];
                string orderId = command[2];

                string rpcUrl = cfg["rpc"];
                if (string.IsNullOrWhiteSpace(rpcUrl))
                {
                    msg = "ethereum.rpc is not set";
                    return false;
                }
                var web3 = new Nethereum.Web3.Web3(rpcUrl);

#if DEBUG
                string confirmationsCount = cfg["confirmationsCount"];
                if (int.TryParse(confirmationsCount, out int parsedCount))
                {
                    mConfirmationsExpected = parsedCount;
                }
#endif

                string payTxId;
                string erc20Transfer = null;
                string ethDstAddress = null;
                string amountString = null;
                if (progressToken != null)
                {
                    var progressTokenComponents = progressToken.Split(':');
                    payTxId = progressTokenComponents[0];
                    Debug.Assert(progressTokenComponents.Length == 1 || progressTokenComponents.Length == 4);
                    if (progressTokenComponents.Length == 4)
                    {
                        erc20Transfer = progressTokenComponents[1];
                        ethDstAddress = progressTokenComponents[2];
                        amountString = progressTokenComponents[3];
                    }
                }
                else
                {
                    string ethereumPrivateKey;
                    string ethSrcAddress = getSourceAddress(secretOverride, cfg, out ethereumPrivateKey);
                    if (ethSrcAddress == null)
                    {
                        msg = "ethereum.secret is not set";
                        return false;
                    }

                    string srcAddressId = null;
                    string dstAddressId = null;

                    var protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{orderId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }

                    if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix))
                    {
                        var dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                        if (gainString.Equals("0"))
                        {
                            srcAddressId = dealOrder.SrcAddress;
                            dstAddressId = dealOrder.DstAddress;
                        }
                        else
                        {
                            dstAddressId = dealOrder.SrcAddress;
                            srcAddressId = dealOrder.DstAddress;
                        }
                        amountString = dealOrder.Amount;
                    }
                    else if (orderId.StartsWith(RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix))
                    {
                        var repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                        if (gainString.Equals("0"))
                        {
                            srcAddressId = repaymentOrder.SrcAddress;
                            dstAddressId = repaymentOrder.DstAddress;
                        }
                        else
                        {
                            dstAddressId = repaymentOrder.SrcAddress;
                            srcAddressId = repaymentOrder.DstAddress;
                        }
                        amountString = repaymentOrder.Amount;
                    }
                    else
                    {
                        msg = "unexpected referred order";
                        return false;
                    }

                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{srcAddressId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }
                    var srcAddress = Address.Parser.ParseFrom(protobuf);

                    protobuf = RpcHelper.ReadProtobuf(httpClient, $"{url}/state/{dstAddressId}", out msg);
                    if (protobuf == null)
                    {
                        msg = "failed to extract address data through RPC";
                        return false;
                    }
                    Address dstAddress = Address.Parser.ParseFrom(protobuf);

                    if (!srcAddress.Blockchain.Equals(name) || !dstAddress.Blockchain.Equals(name))
                    {
                        msg = $"erc20 RegisterTransfer can only transfer ethereum erc20 tokens.\nThis source is registered for {srcAddress.Blockchain} and destination for {dstAddress.Blockchain}";
                        return false;
                    }

                    var scrAddressSegments = srcAddress.Value.Split('@');
                    var dstAddressSegments = dstAddress.Value.Split('@');
                    if (scrAddressSegments.Length != 2 || dstAddressSegments.Length != 2)
                    {
                        msg = $"erc20 address must be composed of a contract address and a wallet address separated by semicolon.\n Provided values are: {srcAddress.Value} for source and {dstAddress.Value} for destination";
                        return false;
                    }
                    if (!scrAddressSegments[0].Equals(dstAddressSegments[0]))
                    {
                        msg = $"The source and destination contracts must be the same.\n Provided contracts are: {scrAddressSegments[0]} for source and {dstAddressSegments[0]} for destination";
                        return false;
                    }
                    srcAddress.Value = scrAddressSegments[1];
                    dstAddress.Value = dstAddressSegments[1];

                    ethDstAddress = dstAddress.Value;

                    if (!ethSrcAddress.Equals(srcAddress.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        msg = $"The deal is for a different client. Expected {ethSrcAddress}, got {srcAddress.Value}";
                        return false;
                    }

                    BigInteger transferAmount;
                    if (!BigInteger.TryParse(amountString, out transferAmount) || transferAmount <= 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    BigInteger gain;
                    if (!BigInteger.TryParse(gainString, out gain))
                    {
                        msg = "Invalid amount";
                        return false;
                    }
                    transferAmount = transferAmount + gain;
                    if (transferAmount < 0)
                    {
                        msg = "Invalid amount";
                        return false;
                    }

                    TransactionSigner signer = new TransactionSigner();

                    erc20Transfer = scrAddressSegments[0];
                    var erc20TransferContract = web3.Eth.GetContract(erc20TransferAbi, erc20Transfer);

                    var getErc20 = erc20TransferContract.GetFunction("getErc20");
                    string erc20 = getErc20.CallAsync<string>().Result;
                    var erc20Contract = web3.Eth.GetContract(erc20Abi, erc20);
                    var approve = erc20Contract.GetFunction("approve");
                    var approveInput = new object[] { erc20Transfer, transferAmount };

                    string to = erc20;
                    var amount = 0;
                    var txCount = web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(ethSrcAddress).Result;
                    HexBigInteger gasPrice = getGasPrice(web3, cfg);
                    HexBigInteger gasLimit = new HexBigInteger(approve.EstimateGasAsync(approveInput).Result);
                    string data = approve.GetData(approveInput);
                    string txRaw = signer.SignTransaction(ethereumPrivateKey, to, amount, txCount, gasPrice, gasLimit, data);
                    payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;
                    progressToken = $"{payTxId}:{erc20Transfer}:{ethDstAddress}:{amountString}";
                }

                inProgress = true;

                var receipt = web3.Eth.TransactionManager.TransactionReceiptService.PollForReceiptAsync(payTxId).Result; //TODO process separately if tx doesn't exist?
                if (receipt.BlockNumber != null)
                {
                    if (receipt.Status.Value == 0)
                    {
                        msg = $"Failed transcaction {progressToken}";
                        return false;
                    }

                    var blockNumber = web3.Eth.Blocks.GetBlockNumber.SendRequestAsync().Result;
                    if (blockNumber.Value - receipt.BlockNumber.Value >= mConfirmationsExpected)
                    {
                        if (erc20Transfer != null)
                        {
                            string ethereumPrivateKey;
                            string ethSrcAddress = getSourceAddress(secretOverride, cfg, out ethereumPrivateKey);

                            BigInteger transferAmount = BigInteger.Parse(amountString);

                            var erc20TransferContract = web3.Eth.GetContract(erc20TransferAbi, erc20Transfer);
                            var transfer = erc20TransferContract.GetFunction("transfer");
                            var transferInput = new object[] { ethDstAddress, transferAmount, orderId };

                            string to = erc20Transfer;
                            var amount = 0;
                            var txCount = web3.Eth.Transactions.GetTransactionCount.SendRequestAsync(ethSrcAddress).Result;
                            HexBigInteger gasPrice = getGasPrice(web3, cfg);
                            HexBigInteger gasLimit = new HexBigInteger(transfer.EstimateGasAsync(transferInput).Result);
                            string data = transfer.GetData(transferInput);

                            TransactionSigner signer = new TransactionSigner();
                            string txRaw = signer.SignTransaction(ethereumPrivateKey, to, amount, txCount, gasPrice, gasLimit, data);
                            payTxId = web3.Eth.Transactions.SendRawTransaction.SendRequestAsync("0x" + txRaw).Result;

                            progressToken = payTxId;
                        }
                        else
                        {
                            progressToken = null;
                        }
                    }
                }

                if (progressToken != null)
                {
                    msg = null;
                    return true;
                }

                command = new string[] { command[0], gainString, orderId, payTxId };
                var tx = txBuilder.BuildTx(command, out msg);
                Debug.Assert(tx != null);
                Debug.Assert(msg == null);

                var content = new ByteArrayContent(tx);
                content.Headers.Add("Content-Type", "application/octet-stream");

                msg = RpcHelper.CompleteBatch(httpClient, url, "batches", content, txid, out link);

                inProgress = false;

                if (msg != null)
                {
                    link = null;
                }
                else
                {
                    Debug.Assert(link != null);
                }

                return true;
            }
            else
            {
                inProgress = false;
                msg = "Unknown command: " + command[0];
                return false;
            }
        }

        private static string getSourceAddress(string secretOverride, IConfiguration cfg, out string ethereumPrivateKey)
        {
            ethereumPrivateKey = secretOverride ?? cfg["secret"];
            if (string.IsNullOrWhiteSpace(ethereumPrivateKey))
                return null;
            return EthECKey.GetPublicAddress(ethereumPrivateKey);
        }

        private static HexBigInteger getGasPrice(Nethereum.Web3.Web3 web3, IConfiguration cfg)
        {
            HexBigInteger gasPrice;

            string gasPriceInGweiString = cfg["gasPriceInGwei"];
            if (int.TryParse(gasPriceInGweiString, out int gasPriceOverride))
            {
                gasPrice = new HexBigInteger(Nethereum.Util.UnitConversion.Convert.ToWei(gasPriceOverride, Nethereum.Util.UnitConversion.EthUnit.Gwei));
            }
            else
            {
                gasPrice = web3.Eth.GasPrice.SendRequestAsync().Result;
            }

            return gasPrice;
        }
    }
}