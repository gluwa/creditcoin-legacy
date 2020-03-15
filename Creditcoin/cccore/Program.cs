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
using Sawtooth.Sdk.Client;
using System;
using System.Net.Http;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.Numerics;
using System.Linq;

namespace cccore
{
    public class Core
    {
        private const string ERROR = "error";
        private const string DATA = "data";
        private const string ADDRESS = "address";
        private const string MESSAGE = "message";
        private const string PAGING = "paging";
        private const string NEXT = "next";

        public static string Run(HttpClient httpClient, string creditcoinUrl, string link, bool txid)
        {
            try
            {
                return RpcHelper.CompleteBatch(httpClient, creditcoinUrl, link, txid);
            }
            catch (Exception x)
            {
                return $"Error (unexpected): {x.Message}"; //TODO: consolidate error handling
            }
        }

        public static List<string> Run(HttpClient httpClient, string creditcoinUrl, string[] args, IConfiguration config, bool txid, string pluginFolder, string progressToken, Signer signer, out bool inProgress, string secretOverride, out string link)
        {
            link = null;

            var ret = new List<string>();
            inProgress = false;
            try
            {
                string action = args[0].ToLower();
                string[] command = args.Skip(1).ToArray();

                bool success = true;
                if (action.Equals("sighash"))
                {
                    ret.Add(TxBuilder.getSighash(signer));
                    ret.Add("Success");
                }
                else if (action.Equals("tip"))
                {
                    BigInteger headIdx = GetHeadIdx(httpClient, creditcoinUrl);
                    if (command.Length == 1)
                    {
                        BigInteger num;
                        if (!BigInteger.TryParse(command[0], out num))
                        {
                            throw new Exception("Invalid numerics");
                        }
                        headIdx -= num;
                    }
                    ret.Add(headIdx.ToString());
                    ret.Add("Success");
                }
                else if (action.Equals("list"))
                {
                    if (command.Length > 2)
                    {
                        throw new Exception("1 or 2 parameters expected");
                    }
                    string id = null;
                    if (command.Length == 2)
                    {
                        id = command[1];
                    }
                    if (command[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.settingNamespace, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Setting setting = Setting.Parser.ParseFrom(protobuf);
                                foreach (var entry in setting.Entries)
                                {
                                    ret.Add($"{entry.Key}: {entry.Value}");
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("wallets", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.walletPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                                ret.Add($"wallet({objid}) amount:{wallet.Amount}");
                            }
                        });
                    }
                    else if (command[0].Equals("addresses", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Address address = Address.Parser.ParseFrom(protobuf);
                                ret.Add($"address({objid}) blockchain:{quote(address.Blockchain)} value:{quote(address.Value)} network:{quote(address.Network)} sighash:{address.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("transfers", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.transferPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                                ret.Add($"transfer({objid}) blockchain:{quote(transfer.Blockchain)} srcAddress:{transfer.SrcAddress} dstAddress:{transfer.DstAddress} order:{transfer.Order} amount:{transfer.Amount} tx:{transfer.Tx} block:{transfer.Block} processed:{transfer.Processed} sighash:{transfer.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("askOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                                ret.Add($"askOrder({objid}) blockchain:{quote(askOrder.Blockchain)} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("bidOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                                ret.Add($"bidOrder({objid}) blockchain:{quote(bidOrder.Blockchain)} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("offers", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.offerPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                Offer offer = Offer.Parser.ParseFrom(protobuf);
                                ret.Add($"offer({objid}) blockchain:{quote(offer.Blockchain)} askOrder:{offer.AskOrder} bidOrder:{offer.BidOrder} expiration:{offer.Expiration} block:{offer.Block}");
                            }
                        });
                    }
                    else if (command[0].Equals("dealOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                                ret.Add($"dealOrder({objid}) blockchain:{quote(dealOrder.Blockchain)} srcAddress:{dealOrder.SrcAddress} dstAddress:{dealOrder.DstAddress} amount:{dealOrder.Amount} interest:{dealOrder.Interest} maturity:{dealOrder.Maturity} fee:{dealOrder.Fee} expiration:{dealOrder.Expiration} block:{dealOrder.Block} loanTransfer:{(dealOrder.LoanTransfer.Equals(string.Empty) ? "*" : dealOrder.LoanTransfer)} repaymentTransfer:{(dealOrder.RepaymentTransfer.Equals(string.Empty) ? "*" : dealOrder.RepaymentTransfer)} lock:{(dealOrder.Lock.Equals(string.Empty) ? "*" : dealOrder.Lock)} sighash:{dealOrder.Sighash}");
                            }
                        });
                    }
                    else if (command[0].Equals("repaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            if (id == null || id != null && id.Equals(objid))
                            {
                                RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                                ret.Add($"repaymentOrder({objid}) blockchain:{quote(repaymentOrder.Blockchain)} srcAddress:{repaymentOrder.SrcAddress} dstAddress:{repaymentOrder.DstAddress} amount:{repaymentOrder.Amount} expiration:{repaymentOrder.Expiration} block:{repaymentOrder.Block} deal:{repaymentOrder.Deal} previousOwner:{(repaymentOrder.PreviousOwner.Equals(string.Empty)? "*": repaymentOrder.PreviousOwner)} transfer:{(repaymentOrder.Transfer.Equals(string.Empty) ? "*" : repaymentOrder.Transfer)} sighash:{repaymentOrder.Sighash}");
                            }
                        });
                    }
                    else
                    {
                        ret.Add("Invalid command " + command[0]);
                        success = false;
                    }

                    if (success)
                    {
                        ret.Add("Success");
                    }
                }
                else if (action.Equals("show"))
                {
                    BigInteger headIdx = GetHeadIdx(httpClient, creditcoinUrl);

                    if (command.Length <= 1) throw new Exception("1 or more parameters expected");
                    string sighash;
                    if (command[1].Equals("0"))
                    {
                        sighash = TxBuilder.getSighash(signer);
                    }
                    else
                    {
                        sighash = command[1];
                    }

                    if (command[0].Equals("balance", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");
                        string prefix = RpcHelper.creditCoinNamespace + RpcHelper.walletPrefix;
                        string id = prefix + sighash;
                        string amount = "0";
                        filter(httpClient, creditcoinUrl, ret, prefix, (string objid, byte[] protobuf) =>
                        {
                            Wallet wallet = Wallet.Parser.ParseFrom(protobuf);
                            if (objid.Equals(id))
                            {
                                amount = wallet.Amount;
                            }
                        });
                        ret.Add($"{amount}");
                    }
                    else if (command[0].Equals("address", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 5) throw new Exception("5 parameters expected");
                        var blockchain = command[2].ToLower();
                        var addr = command[3];
                        var network = command[4].ToLower();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            if (address.Sighash == sighash && address.Blockchain == blockchain && address.Value == addr && address.Network == network)
                            {
                                ret.Add(objid);
                            }
                        });
                    }
                    else if (command[0].Equals("matchingOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        var askOrders = new Dictionary<string, AskOrder>();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.askOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            AskOrder askOrder = AskOrder.Parser.ParseFrom(protobuf);
                            BigInteger block;
                            if (!BigInteger.TryParse(askOrder.Block, out block))
                            {
                                throw new Exception("Invalid numerics");
                            }
                            if (block + askOrder.Expiration > headIdx)
                            {
                                askOrders.Add(objid, askOrder);
                            }
                        });
                        var bidOrders = new Dictionary<string, BidOrder>();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            BigInteger block;
                            if (!BigInteger.TryParse(bidOrder.Block, out block))
                            {
                                throw new Exception("Invalid numerics");
                            }
                            if (block + bidOrder.Expiration > headIdx)
                            {
                                bidOrders.Add(objid, bidOrder);
                            }
                        });

                        match(sighash, askOrders, bidOrders, ret);
                    }
                    else if (command[0].Equals("currentOffers", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        var bidOrders = new Dictionary<string, BidOrder>();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.bidOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            BidOrder bidOrder = BidOrder.Parser.ParseFrom(protobuf);
                            bidOrders.Add(objid, bidOrder);
                        });

                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.offerPrefix, (string objid, byte[] protobuf) =>
                        {
                            Offer offer = Offer.Parser.ParseFrom(protobuf);
                            BidOrder bidOrder = bidOrders[offer.BidOrder];
                            if (bidOrder.Sighash == sighash)
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(offer.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + offer.Expiration > headIdx)
                                {
                                    ret.Add(objid);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("creditHistory", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        filterDeals(httpClient, creditcoinUrl, ret, null, sighash, (string dealAddress, DealOrder dealOrder) =>
                        {
                            var status = dealOrder.LoanTransfer.Equals(string.Empty) ? "NEW" : "COMPLETE";
                            if (!dealOrder.RepaymentTransfer.Equals(string.Empty))
                            {
                                status = "CLOSED";
                            }
                            ret.Add($"status:{status}, amount:{dealOrder.Amount}, blockchain:{quote(dealOrder.Blockchain)}");
                        });
                    }
                    else if (command[0].Equals("newDeals", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        filterDeals(httpClient, creditcoinUrl, ret, sighash, null, (string dealAddress, DealOrder dealOrder) =>
                        {
                            if (dealOrder.LoanTransfer.Equals(string.Empty))
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(dealOrder.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + dealOrder.Expiration > headIdx)
                                {
                                    ret.Add(dealAddress);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("transfer", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 3) throw new Exception("3 parameters expected");
                        var orderId = command[2];

                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.transferPrefix, (string objid, byte[] protobuf) =>
                        {
                            Transfer transfer = Transfer.Parser.ParseFrom(protobuf);
                            if (transfer.Sighash == sighash && transfer.Order == orderId && !transfer.Processed)
                                ret.Add(objid);
                        });
                    }
                    else if (command[0].Equals("currentLoans", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        filterDeals(httpClient, creditcoinUrl, ret, null, sighash, (string dealAddress, DealOrder dealOrder) =>
                        {
                            if (!dealOrder.LoanTransfer.Equals(string.Empty) && dealOrder.RepaymentTransfer.Equals(string.Empty))
                            {
                                ret.Add(dealAddress);
                            }
                        });
                    }
                    else if (command[0].Equals("lockedLoans", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        filterDeals(httpClient, creditcoinUrl, ret, null, sighash, (string dealAddress, DealOrder dealOrder) =>
                        {
                            if (!dealOrder.Lock.Equals(string.Empty))
                            {
                                ret.Add(dealAddress);
                            }
                        });
                    }
                    else if (command[0].Equals("newRepaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (command.Length != 2) throw new Exception("2 parameters expected");

                        var addresses = new Dictionary<string, Address>();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
                        {
                            Address address = Address.Parser.ParseFrom(protobuf);
                            addresses.Add(objid, address);
                        });

                        var dealOrders = new Dictionary<string, DealOrder>();
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                            dealOrders.Add(objid, dealOrder);
                        });

                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            DealOrder deal = dealOrders[repaymentOrder.Deal];
                            Address address = addresses[deal.SrcAddress];
                            if (repaymentOrder.Transfer.Equals(string.Empty) && repaymentOrder.PreviousOwner.Equals(string.Empty) && address.Sighash.Equals(sighash))
                            {
                                BigInteger block;
                                if (!BigInteger.TryParse(repaymentOrder.Block, out block))
                                {
                                    throw new Exception("Invalid numerics");
                                }
                                if (block + repaymentOrder.Expiration > headIdx)
                                {
                                    ret.Add(objid);
                                }
                            }
                        });
                    }
                    else if (command[0].Equals("currentRepaymentOrders", StringComparison.OrdinalIgnoreCase))
                    {
                        filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.repaymentOrderPrefix, (string objid, byte[] protobuf) =>
                        {
                            RepaymentOrder repaymentOrder = RepaymentOrder.Parser.ParseFrom(protobuf);
                            if (repaymentOrder.Transfer.Equals(string.Empty) && !repaymentOrder.PreviousOwner.Equals(string.Empty) && repaymentOrder.Sighash.Equals(sighash))
                            {
                                ret.Add(objid);
                            }
                        });
                    }
                    else
                    {
                        ret.Add("Invalid command " + command[0]);
                        success = false;
                    }

                    if (success)
                    {
                        ret.Add("Success");
                    }
                }
                else
                {
                    var txBuilder = new TxBuilder(signer);
                    if (action.Equals("creditcoin"))
                    {
                        string msg;
                        var tx = txBuilder.BuildTx(command, out msg);
                        if (tx == null)
                        {
                            Debug.Assert(msg != null);
                            ret.Add(msg);
                        }
                        else
                        {
                            Debug.Assert(msg == null);
                            var content = new ByteArrayContent(tx);
                            content.Headers.Add("Content-Type", "application/octet-stream");

                            msg = RpcHelper.CompleteBatch(httpClient, creditcoinUrl, "batches", content, txid, out link);
                            Debug.Assert(msg != null || msg == null && link != null);
                            if (msg == null)
                                return null;
                            ret.Add(msg);
                        }
                    }
                    else
                    {
                        var loader = new Loader<ICCClientPlugin>();
                        var msgs = new List<string>();
                        loader.Load(pluginFolder, msgs);
                        foreach (var msg in msgs)
                        {
                            ret.Add(msg);
                        }

                        ICCClientPlugin plugin = loader.Get(action);
                        var pluginConfig = config.GetSection(action);
                        if (plugin == null)
                        {
                            ret.Add("Error: Unknown action " + action); //TODO: consolidate error handling
                        }
                        else
                        {
                            bool done = plugin.Run(txid, pluginConfig, secretOverride, httpClient, txBuilder, ref progressToken, creditcoinUrl, command, out inProgress, out string msg, out link);
                            if (done)
                            {
                                if (link != null)
                                    return null;

                                if (progressToken != null)
                                {
                                    ret.Add($"{progressToken}");
                                }
                                if (msg == null)
                                {
                                    msg = "Success";
                                }
                                ret.Add(msg);
                            }
                            else
                            {
                                ret.Add("Error: " + msg); //TODO: consolidate error handling
                            }
                        }
                    }
                }
            }
            catch (Exception x)
            {
                ret.Add($"Error (unexpected): {x.Message}"); //TODO: consolidate error handling
            }
            return ret;
        }

        public static BigInteger GetHeadIdx(HttpClient httpClient, string creditcoinUrl)
        {
            string msg;
            string head = RpcHelper.LastBlock(httpClient, creditcoinUrl, out msg);
            Debug.Assert(head != null && msg == null || head == null && msg != null);
            if (head == null)
            {
                throw new Exception(msg);
            }
            BigInteger headIdx;
            if (!BigInteger.TryParse(head, out headIdx))
            {
                throw new Exception("Invalid numerics");
            }

            return headIdx;
        }

        public static string quote(string str)
        {
            if (str.IndexOf(' ') == -1 && str.IndexOf('"') == -1)
                return str;
            return $"\"{str.Replace("\"", "\\\"")}\"";
        }

        public static string unquote(string str)
        {
            if (str.StartsWith('"'))
            {
                if (!str.EndsWith('"'))
                    throw new Exception($"Unexpected quoted string, no end quote: {str}");
                str = str.Substring(1, str.Length - 1);
                if (str.IndexOf(' ') == -1 && str.IndexOf('"') == -1)
                    throw new Exception($"Unexpected quoted string, contains no spaces or quotes: {str}");
                return str.Replace("\\\"", "\"");
            }
            if (str.IndexOf(' ') != -1 || str.IndexOf('"') != -1)
                throw new Exception($"Unexpected quoted string, must have been in quotes, but is not: {str}");
            return str;
        }

        public static Dictionary<string, string> getSettings(HttpClient httpClient, string creditcoinUrl, List<string> ret)
        {
            var settings = new Dictionary<string, string>();
            filter(httpClient, creditcoinUrl, ret, RpcHelper.settingNamespace, (string address, byte[] protobuf) =>
            {
                Setting setting = Setting.Parser.ParseFrom(protobuf);
                foreach (var entry in setting.Entries)
                {
                    settings.Add(entry.Key, entry.Value);
                }
            });
            return settings;
        }

        public static Signer getSigner(IConfiguration config, string secret)
        {
            string signerHexStr = secret ?? config["signer"];
            if (string.IsNullOrWhiteSpace(signerHexStr))
            {
                throw new Exception("Signer is not configured");
            }
            try
            {
                return new Signer(RpcHelper.HexToBytes(signerHexStr));
            }
            catch (Exception x)
            {
                throw new Exception("Failed to initialize signer: " + x.Message);
            }
        }

        public static void filterDeals(HttpClient httpClient, string creditcoinUrl, List<string> ret, string investorSighash, string fundraiserSighash, Action<string, DealOrder> lister)
        {
            var dealOrders = new Dictionary<string, DealOrder>();
            filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.dealOrderPrefix, (string objid, byte[] protobuf) =>
            {
                DealOrder dealOrder = DealOrder.Parser.ParseFrom(protobuf);
                dealOrders.Add(objid, dealOrder);
            });
            var addresses = new Dictionary<string, Address>();
            filter(httpClient, creditcoinUrl, ret, RpcHelper.creditCoinNamespace + RpcHelper.addressPrefix, (string objid, byte[] protobuf) =>
            {
                Address address = Address.Parser.ParseFrom(protobuf);
                addresses.Add(objid, address);
            });

            foreach (var dealOrderEntry in dealOrders)
            {
                DealOrder dealOrder = dealOrderEntry.Value;
                if (fundraiserSighash != null)
                {
                    Address address = addresses[dealOrder.DstAddress];
                    if (address.Sighash == fundraiserSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder);
                    }
                }
                if (investorSighash != null)
                {
                    Address address = addresses[dealOrder.SrcAddress];
                    if (address.Sighash == investorSighash)
                    {
                        lister(dealOrderEntry.Key, dealOrder);
                    }
                }
            }
        }

        public static void match(string sighash, Dictionary<string, AskOrder> askOrders, Dictionary<string, BidOrder> bidOrders, List<string> ret)
        {
            foreach (var askOrderEntry in askOrders)
            {
                foreach (var bidOrderEntry in bidOrders)
                {
                    if (!askOrderEntry.Value.Sighash.Equals(sighash))
                    {
                        break;
                    }

                    BigInteger askAmount, bidAmount, askInterest, bidInterest, askMaturity, bidMaturity, askFee, bidFee;
                    if (!BigInteger.TryParse(askOrderEntry.Value.Amount, out askAmount) || !BigInteger.TryParse(bidOrderEntry.Value.Amount, out bidAmount) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Interest, out askInterest) || !BigInteger.TryParse(bidOrderEntry.Value.Interest, out bidInterest) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Maturity, out askMaturity) || !BigInteger.TryParse(bidOrderEntry.Value.Maturity, out bidMaturity) ||
                        !BigInteger.TryParse(askOrderEntry.Value.Fee, out askFee) || !BigInteger.TryParse(bidOrderEntry.Value.Fee, out bidFee))
                    {
                        ret.Add("Error: Invalid numerics"); //TODO: consolidate error handling
                        return;
                    }

                    if (askAmount == bidAmount && askInterest / askMaturity <= bidInterest / bidMaturity && askFee <= bidFee)
                    {
                        ret.Add($"{askOrderEntry.Key} {bidOrderEntry.Key}");
                    }
                }
            }
        }

        public static void filter(HttpClient httpClient, string creditcoinUrl, List<string> ret, string prefix, Action<string, byte[]> lister)
        {
            var url = $"{creditcoinUrl}/state?address={prefix}";
            for (; ; )
            {
                using (HttpResponseMessage responseMessage = httpClient.GetAsync(url).Result)
                {
                    var json = responseMessage.Content.ReadAsStringAsync().Result;
                    var response = JObject.Parse(json);
                    if (response.ContainsKey(ERROR))
                    {
                        ret.Add($"Error: {(string)response[ERROR][MESSAGE]}"); //TODO: consolidate error handling
                    }
                    else
                    {
                        Debug.Assert(response.ContainsKey(DATA));
                        var data = response[DATA];
                        foreach (var datum in data)
                        {
                            Debug.Assert(datum.Type == JTokenType.Object);
                            var obj = (JObject)datum;
                            Debug.Assert(obj.ContainsKey(ADDRESS));
                            var objid = (string)obj[ADDRESS];
                            Debug.Assert(obj.ContainsKey(DATA));
                            var content = (string)obj[DATA];
                            byte[] protobuf = Convert.FromBase64String(content);
                            lister(objid, protobuf);
                        }

                        Debug.Assert(response.ContainsKey(PAGING));
                        var paging = (JObject)response[PAGING];
                        if (!paging.ContainsKey(NEXT))
                            break;
                        url = (string)paging[NEXT];
                    }
                }
            }
        }
    }
}