using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Sawtooth.Sdk.Client;

namespace ccaas.Controllers
{
    [Route("api")]
    [ApiController]
    public class CreditcoinController : Controller
    {
        public static IConfiguration config;
        public static string pluginFolder;
        public static string creditcoinUrl;
        private static HttpClient httpClient = new HttpClient();

        private const string keyIsMissing = "Key is missing";
        private const string missingPrameters = "Missing required parameter(s)";
        private const string unexpectedError = "Error (unexpected)";
        private const string invalidSighash = "Invalid sighash format";

        [HttpGet("RetrieveAccount/{sighash}")]
        public IActionResult GetRetrieveAccount(string sighash)
        {
            //show Balance sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Balance");
            args.Add(sighash);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count != 2 || output[0].StartsWith("Error") || !output[1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new ccaas.Models.Account() { amount = output[0] };
            return Json(ret);
        }

        [HttpPost("Sighash")]
        public IActionResult PostSighash(Models.SighashQueryParam queryParam)
        {
            //sighash
            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);
            var sighash = ccplugin.TxBuilder.getSighash(signer);

            return Ok(sighash);
        }

        [HttpGet("CheckCommittedStatus")]
        public IActionResult GetCheckIfCommitted(string link)
        {
            var msg = cccore.Core.Run(httpClient, creditcoinUrl, HttpUtility.UrlDecode(link), false);
            if (msg == null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = link });
            if (!msg.Equals("Success"))
                return statusCodeByMsg(msg);
            return Ok();
        }

        [HttpPost("RegisterAddress")]
        public IActionResult PostRegisterAddress(Models.RegisterAddressQueryParam queryParam)
        {
            //creditcoin RegisterAddress blockchain address network
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("RegisterAddress");
            args.Add(queryParam.query.blockchain);
            if (queryParam.query.blockchain.Equals("erc20"))
            {
                if (string.IsNullOrWhiteSpace(queryParam.query.erc20))
                    return StatusCode(400, "expecting erc20 parameter for erc20 blockchain");
                args.Add($"{queryParam.query.erc20}@{queryParam.query.address}");
            }
            else
            {
                args.Add(queryParam.query.address);
            }
            args.Add(queryParam.query.network);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("ShowAddress/{sighash}")]
        public IActionResult GetShowAddress(string sighash, string blockchain, string address, string network, string erc20 = null)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Address");
            args.Add(sighash);
            args.Add(blockchain);
            if (blockchain.Equals("erc20"))
            {
                if (string.IsNullOrWhiteSpace(erc20))
                    return StatusCode(400, missingPrameters);
                args.Add($"{erc20}@{address}");
            }
            else
            {
                args.Add(address);
            }
            args.Add(network);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);

            if (output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok(output[0]);
        }

        [HttpPost("CreateAskOrder")]
        public IActionResult PostCreateAskOrder(Models.CreateOrderQueryParam queryParam)
        {
            //creditcoin AddAskOrder addressId amount interest maturity fee expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddAskOrder");
            args.Add(queryParam.query.addressId);
            args.Add(queryParam.query.term.amount);
            args.Add(queryParam.query.term.interest);
            args.Add(queryParam.query.term.maturity);
            args.Add(queryParam.query.term.fee);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("CreateBidOrder")]
        public IActionResult PostCreateBidOrder(Models.CreateOrderQueryParam queryParam)
        {
            //creditcoin AddBidOrder addressId amount interest maturity fee expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddBidOrder");
            args.Add(queryParam.query.addressId);
            args.Add(queryParam.query.term.amount);
            args.Add(queryParam.query.term.interest);
            args.Add(queryParam.query.term.maturity);
            args.Add(queryParam.query.term.fee);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("MatchingOrders/{sighash}")]
        public IActionResult GetMatchingOrders(string sighash)
        {
            //show MatchingOrders sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("MatchingOrders");
            args.Add(sighash);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new List<ccaas.Models.MatchingOrders>();
            for (int i = 0; i < output.Count - 1; ++i)
            {
                var ids = output[i].Split(' ');
                if (ids.Length != 2)
                    return StatusCode(503, unexpectedError);

                //list AskOrders id
                var argsAskOrder = new List<string>();
                argsAskOrder.Add("list");
                argsAskOrder.Add("AskOrders");
                argsAskOrder.Add(ids[0]);
                List<string> outputAskOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsAskOrder.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputAskOrders.Count != 2 || outputAskOrders[0].StartsWith("Error") || !outputAskOrders[1].Equals("Success"))
                    return statusCodeByMsg(outputAskOrders[0]);
                var askOrderComponents = outputAskOrders[0].Split(' ');
                if (askOrderComponents.Length != 10)
                    return StatusCode(503, unexpectedError);

                //ret.Add($"askOrder({objid}) blockchain:{askOrder.Blockchain} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                var askOrderId = ids[0];
                var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                for (int j = 1; j < askOrderComponents.Length; ++j) // skip the first component - askOrder(objId)
                {
                    var pair = askOrderComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("address"))
                        askOrder.order.addressId = pair[1];
                    else if (pair[0].Equals("amount"))
                        askOrder.order.term.amount = pair[1];
                    else if (pair[0].Equals("interest"))
                        askOrder.order.term.interest = pair[1];
                    else if (pair[0].Equals("maturity"))
                        askOrder.order.term.maturity = pair[1];
                    else if (pair[0].Equals("fee"))
                        askOrder.order.term.fee = pair[1];
                    else if (pair[0].Equals("expiration"))
                        askOrder.order.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        askOrder.block = pair[1];
                }

                //list bidOrders id
                var argsBidOrder = new List<string>();
                argsBidOrder.Add("list");
                argsBidOrder.Add("BidOrders");
                argsBidOrder.Add(ids[1]);
                List<string> outputBidOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsBidOrder.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputBidOrders.Count != 2 || outputBidOrders[0].StartsWith("Error") || !outputBidOrders[1].Equals("Success"))
                    return statusCodeByMsg(outputBidOrders[0]);
                var bidOrderComponents = outputBidOrders[0].Split(' ');
                if (bidOrderComponents.Length != 10)
                    return StatusCode(503, unexpectedError);

                //ret.Add($"bidOrder({objid}) blockchain:{bidOrder.Blockchain} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                var bidOrderId = ids[1];
                var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                for (int j = 1; j < bidOrderComponents.Length; ++j) // skip the first component - bidOrder(objId)
                {
                    var pair = bidOrderComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("address"))
                        bidOrder.order.addressId = pair[1];
                    else if (pair[0].Equals("amount"))
                        bidOrder.order.term.amount = pair[1];
                    else if (pair[0].Equals("interest"))
                        bidOrder.order.term.interest = pair[1];
                    else if (pair[0].Equals("maturity"))
                        bidOrder.order.term.maturity = pair[1];
                    else if (pair[0].Equals("fee"))
                        bidOrder.order.term.fee = pair[1];
                    else if (pair[0].Equals("expiration"))
                        bidOrder.order.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        bidOrder.block = pair[1];
                }

                var matchingOrders = new ccaas.Models.MatchingOrders() { askOrder = askOrder, bidOrder = bidOrder };
                ret.Add(matchingOrders);
            }
            return Json(ret);
        }

        [HttpPost("CreateOffer")]
        public IActionResult PostCreateOffer(Models.CreateOfferQueryParam queryParam)
        {
            //creditcoin AddOffer askOrderId bidOrderId expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddOffer");
            args.Add(queryParam.query.askOrderId);
            args.Add(queryParam.query.bidOrderId);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("RetrieveOffers/{sighash}")]
        public IActionResult GetRetrieveOffers(string sighash)
        {
            //show CurrentOffers sighash
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("CurrentOffers");
            args.Add(sighash);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new List<ccaas.Models.OfferResponse>();
            for (int i = 0; i < output.Count - 1; ++i)
            {
                //list Offers id
                var argsOffer = new List<string>();
                argsOffer.Add("list");
                argsOffer.Add("Offers");
                argsOffer.Add(output[i]);
                List<string> outputOffers = cccore.Core.Run(httpClient, creditcoinUrl, argsOffer.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputOffers.Count < 2 || outputOffers[0].StartsWith("Error") || !outputOffers[outputOffers.Count - 1].Equals("Success"))
                    return statusCodeByMsg(outputOffers[1]);
                var offerComponents = outputOffers[0].Split(' ');
                if (offerComponents.Length != 6)
                    return StatusCode(503, unexpectedError);
                //ret.Add($"offer({objid}) blockchain:{offer.Blockchain} askOrder:{offer.AskOrder} bidOrder:{offer.BidOrder} expiration:{offer.Expiration} block:{offer.Block}");
                string askOrderId = null;
                string bidOrderId = null;
                var offer = new ccaas.Models.OfferResponse() { id = output[i] };
                for (int j = 1; j < offerComponents.Length; ++j) // skip the first component - offer(objId)
                {
                    var pair = offerComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("askOrder"))
                        askOrderId = pair[1];
                    else if (pair[0].Equals("bidOrder"))
                        bidOrderId = pair[1];
                    else if (pair[0].Equals("expiration"))
                        offer.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        offer.block = pair[1];
                }

                //list AskOrders id
                var argsAskOrder = new List<string>();
                argsAskOrder.Add("list");
                argsAskOrder.Add("AskOrders");
                argsAskOrder.Add(askOrderId);
                List<string> outputAskOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsAskOrder.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputAskOrders.Count != 2 || outputAskOrders[0].StartsWith("Error") || !outputAskOrders[1].Equals("Success"))
                    return statusCodeByMsg(outputAskOrders[0]);
                var askOrderComponents = outputAskOrders[0].Split(' ');
                if (askOrderComponents.Length != 10)
                    return StatusCode(503, unexpectedError);

                //ret.Add($"askOrder({objid}) blockchain:{askOrder.Blockchain} address:{askOrder.Address} amount:{askOrder.Amount} interest:{askOrder.Interest} maturity:{askOrder.Maturity} fee:{askOrder.Fee} expiration:{askOrder.Expiration} block:{askOrder.Block} sighash:{askOrder.Sighash}");
                var askOrder = new Models.OrderResponse() { id = askOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                for (int j = 1; j < askOrderComponents.Length; ++j) // skip the first component - askOrder(id)
                {
                    var pair = askOrderComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("address"))
                        askOrder.order.addressId = pair[1];
                    else if (pair[0].Equals("amount"))
                        askOrder.order.term.amount = pair[1];
                    else if (pair[0].Equals("interest"))
                        askOrder.order.term.interest = pair[1];
                    else if (pair[0].Equals("maturity"))
                        askOrder.order.term.maturity = pair[1];
                    else if (pair[0].Equals("fee"))
                        askOrder.order.term.fee = pair[1];
                    else if (pair[0].Equals("expiration"))
                        askOrder.order.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        askOrder.block = pair[1];
                }

                //list bidOrders id
                var argsBidOrder = new List<string>();
                argsBidOrder.Add("list");
                argsBidOrder.Add("BidOrders");
                argsBidOrder.Add(bidOrderId);
                List<string> outputBidOrders = cccore.Core.Run(httpClient, creditcoinUrl, argsBidOrder.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputBidOrders.Count != 2 || outputBidOrders[0].StartsWith("Error") || !outputBidOrders[1].Equals("Success"))
                    return statusCodeByMsg(outputBidOrders[0]);
                var bidOrderComponents = outputBidOrders[0].Split(' ');
                if (bidOrderComponents.Length != 10)
                    return StatusCode(503, unexpectedError);

                //ret.Add($"bidOrder({objid}) blockchain:{bidOrder.Blockchain} address:{bidOrder.Address} amount:{bidOrder.Amount} interest:{bidOrder.Interest} maturity:{bidOrder.Maturity} fee:{bidOrder.Fee} expiration:{bidOrder.Expiration} block:{bidOrder.Block} sighash:{bidOrder.Sighash}");
                var bidOrder = new Models.OrderResponse() { id = bidOrderId, order = new Models.OrderQuery() { term = new Models.Term() } };
                for (int j = 1; j < bidOrderComponents.Length; ++j) // skip the first component - bidOrder(id)
                {
                    var pair = bidOrderComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("address"))
                        bidOrder.order.addressId = pair[1];
                    else if (pair[0].Equals("amount"))
                        bidOrder.order.term.amount = pair[1];
                    else if (pair[0].Equals("interest"))
                        bidOrder.order.term.interest = pair[1];
                    else if (pair[0].Equals("maturity"))
                        bidOrder.order.term.maturity = pair[1];
                    else if (pair[0].Equals("fee"))
                        bidOrder.order.term.fee = pair[1];
                    else if (pair[0].Equals("expiration"))
                        bidOrder.order.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        bidOrder.block = pair[1];
                }

                offer.askOrder = askOrder;
                offer.bidOrder = bidOrder;
                ret.Add(offer);
            }
            return Json(ret);
        }

        [HttpPost("CreateDealOrder")]
        public IActionResult PostCreateDealOrder(Models.CreateDealQueryParam queryParam)
        {
            //creditcoin AddDealOrder offerId expiration
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("AddDealOrder");
            args.Add(queryParam.query.offerId);
            args.Add(queryParam.query.expiration);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpGet("RetrieveDeals/{sighash}")]
        public IActionResult GetRetrieveDeals(string sighash)
        {
            //show NewDeals sighash
            return GetDeals(sighash, "NewDeals");
        }

        [HttpGet("RetrieveLoans/{sighash}")]
        public IActionResult GetRetrieveLoans(string sighash)
        {
            //show CurrentLoans sighash
            return GetDeals(sighash, "CurrentLoans");
        }

        [HttpGet("RetrieveLockedLoans/{sighash}")]
        public IActionResult GetRetrieveLockedLoans(string sighash)
        {
            //show CurrentLoans sighash
            return GetDeals(sighash, "LockedLoans");
        }

        private IActionResult GetDeals(string sighash, string filter)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add(filter);
            args.Add(sighash);
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new List<ccaas.Models.DealResponse>();
            for (int i = 0; i < output.Count - 1; ++i)
            {
                //list Deals id
                var argsDeal = new List<string>();
                argsDeal.Add("list");
                argsDeal.Add("DealOrders");
                argsDeal.Add(output[i]);
                List<string> outputDeals = cccore.Core.Run(httpClient, creditcoinUrl, argsDeal.ToArray(), config, false, pluginFolder, null, null, out ignoreMe, null, out link);
                Debug.Assert(output != null && link == null);
                if (outputDeals.Count < 1 || outputDeals[0].StartsWith("Error") || !outputDeals[outputDeals.Count - 1].Equals("Success"))
                    return statusCodeByMsg(outputDeals[0]);
                var dealComponents = outputDeals[0].Split(' ');
                if (dealComponents.Length != 14)
                    return StatusCode(503, unexpectedError);
                //ret.Add($"dealOrder({objid}) blockchain:{dealOrder.Blockchain} srcAddress:{dealOrder.SrcAddress} dstAddress:{dealOrder.DstAddress} amount:{dealOrder.Amount} interest:{dealOrder.Interest} maturity:{dealOrder.Maturity} fee:{dealOrder.Fee} expiration:{dealOrder.Expiration} block:{dealOrder.Block} loanTransfer:{(dealOrder.LoanTransfer.Equals(string.Empty) ? "*" : dealOrder.LoanTransfer)} repaymentTransfer:{(dealOrder.RepaymentTransfer.Equals(string.Empty) ? "*" : dealOrder.RepaymentTransfer)} lock:{(dealOrder.Lock.Equals(string.Empty) ? "*" : dealOrder.Lock)} sighash:{dealOrder.Sighash}");
                var deal = new ccaas.Models.DealResponse() { id = output[i], term = new Models.Term() };
                for (int j = 1; j < dealComponents.Length; ++j) // skip the first component - dealOrder(objId)
                {
                    var pair = dealComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);
                    if (pair[0].Equals("srcAddress"))
                        deal.srcAddressId = pair[1];
                    else if (pair[0].Equals("dstAddress"))
                        deal.dstAddressId = pair[1];
                    else if (pair[0].Equals("expiration"))
                        deal.expiration = pair[1];
                    else if (pair[0].Equals("block"))
                        deal.block = pair[1];
                    else if (pair[0].Equals("amount"))
                        deal.term.amount = pair[1];
                    else if (pair[0].Equals("interest"))
                        deal.term.interest = pair[1];
                    else if (pair[0].Equals("maturity"))
                        deal.term.maturity = pair[1];
                    else if (pair[0].Equals("fee"))
                        deal.term.fee = pair[1];
                }

                ret.Add(deal);
            }
            return Json(ret);
        }

        [HttpGet("RetrieveAddresses/{sighash}")]
        public IActionResult GetRetrieveAddresses(string sighash)
        {
            //list Addresses
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("list");
            args.Add("Addresses");
            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);
            if (output.Count < 1 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            var ret = new List<ccaas.Models.AddressResponse>();
            for (int i = 0; i < output.Count - 1; ++i)
            {
                var addressComponents = output[i].Split(' ');

                string id;
                {
                    const string addressPrefix = "address(";
                    if (!addressComponents[0].StartsWith(addressPrefix) || !addressComponents[0].EndsWith(")"))
                        return StatusCode(503, unexpectedError);
                    id = addressComponents[0].Substring(addressPrefix.Length, addressComponents[0].Length - addressPrefix.Length - 1);
                }

                var address = new ccaas.Models.AddressResponse() { id = id };

                bool skip = false;
                for (int j = 1; j < addressComponents.Length; ++j) // skip the first component - address(objId)
                {
                    var pair = addressComponents[j].Split(":");
                    if (pair.Length != 2)
                        return StatusCode(503, unexpectedError);

                    if (pair[0].Equals("value"))
                    {
                        address.value = pair[1];
                    }
                    else if (pair[0].Equals("network"))
                    {
                        address.network = pair[1];
                    }
                    else if (pair[0].Equals("sighash"))
                    {
                        if (!pair[1].Equals(sighash))
                            skip = true;
                            break;
                    }
                }
                if (skip)
                    continue;

                ret.Add(address);
            }
            return Json(ret);
        }

        [HttpPost("RegisterTransfer")]
        public ActionResult<string> PostRegisterTransfer(Models.TransferQueryParam queryParam)
        {
            //erc20 RegisterTransfer gain orderId
            // or
            //creditcoin RegisterTransfer gain orderId txid
            string txid = queryParam.query.txid;
            if (txid != null && txid.Equals(string.Empty))
                txid = null;

            if (txid == null)
            {
                if (string.IsNullOrEmpty(queryParam.query.ethKey))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            var args = new List<string>();
            args.Add(txid == null? "erc20": "creditcoin");
            args.Add("RegisterTransfer");
            args.Add(queryParam.query.gain);
            args.Add(queryParam.query.dealOrderId);
            if (txid != null)
                args.Add(txid);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            Signer signer = null;
            if (key != null)
                signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, queryParam.query.ethKey, out string link);
            Debug.Assert(output == null && link != null || link == null);
            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 && output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            if (output.Count == 2)
                return Json(new Models.ContinuationResponse { reason = "waitingForeignBlockchain", waitingForeignBlockchain = output[0] });

            return Ok();
        }

        [HttpPost("CompleteRegisterTransfer")]
        public IActionResult PostCompleteRegisterTransfer(Models.TransferQueryParam queryParam)
        {
            //erc20 RegisterTransfer gain orderId
            var args = new List<string>();
            args.Add("erc20");
            args.Add("RegisterTransfer");
            args.Add(queryParam.query.gain);
            args.Add(queryParam.query.dealOrderId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, queryParam.query.txid, signer, out bool ignoreMe, queryParam.query.ethKey, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 && output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]); //TODO process separately if tx doesn't exist? 404 in docs

            if (output.Count == 2)
                return Json(new Models.ContinuationResponse { reason = "waitingForeignBlockchain", waitingForeignBlockchain = output[0] });

            return Ok();
        }

        [HttpGet("ShowTransfer/{sighash}")]
        public IActionResult GetShowTransfer(string sighash, string dealOrderId)
        {
            if (sighash.Length != 60 || !sighash.All("1234567890abcdef".Contains))
                return BadRequest(invalidSighash);

            var args = new List<string>();
            args.Add("show");
            args.Add("Transfer");
            args.Add(sighash);
            args.Add(dealOrderId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, null, out bool ignoreMe, null, out string link);
            Debug.Assert(output != null && link == null);

            if (output.Count != 2 || output[0].StartsWith("Error") || !output[output.Count - 1].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok(output[0]);
        }

        [HttpPost("UpdateInvestment")]
        public IActionResult PostUpdateInvestment(Models.UpdateQueryParam queryParam)
        {
            //creditcoin CompleteDealOrder dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("CompleteDealOrder");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("LockDealOrder")]
        public IActionResult PostLockDealOrder(Models.LockQueryParam queryParam)
        {
            //creditcoin LockDealOrder dealOrderId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("LockDealOrder");
            args.Add(queryParam.query.dealOrderId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("UpdateRepayment")]
        public IActionResult PostUpdateRepayment(Models.UpdateQueryParam queryParam)
        {
            //creditcoin CloseDealOrder dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("CloseDealOrder");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        [HttpPost("UpdateExemption")]
        public IActionResult PostUpdateExemption(Models.UpdateQueryParam queryParam)
        {
            //creditcoin Exempt dealOrderId transferId
            var args = new List<string>();
            args.Add("creditcoin");
            args.Add("Exempt");
            args.Add(queryParam.query.dealOrderId);
            args.Add(queryParam.query.transferId);

            foreach (var arg in args)
            {
                if (string.IsNullOrWhiteSpace(arg))
                    return StatusCode(400, missingPrameters);
            }

            string key = queryParam.key;
            if (string.IsNullOrWhiteSpace(key))
                return StatusCode(400, keyIsMissing);

            Signer signer = cccore.Core.getSigner(config, key);

            List<string> output = cccore.Core.Run(httpClient, creditcoinUrl, args.ToArray(), config, false, pluginFolder, null, signer, out bool ignoreMe, null, out string link);
            Debug.Assert(output == null && link != null || link == null);

            if (link != null)
                return Json(new Models.ContinuationResponse { reason = "waitingCreditcoinCommit", waitingCreditcoinCommit = HttpUtility.UrlEncode(link) });

            if (output.Count != 1 || output[0].StartsWith("Error") || !output[0].Equals("Success"))
                return statusCodeByMsg(output[0]);

            return Ok();
        }

        private ObjectResult statusCodeByMsg(string error)
        {
            return StatusCode(error.StartsWith(unexpectedError)? 503: 500, error);
        }
    }
}
