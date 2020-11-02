using System;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using ccbe.Models;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace ccbe.Controllers
{
    /// <summary>
    /// Blockchain entries
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class BlockchainController : Controller
    {
        private static Dictionary<int, BigInteger> vesting = new Dictionary<int, BigInteger>()
        {
            { 183, BigInteger.Parse("118244648000000000000000000") },
            { 365, BigInteger.Parse("6263784000000000000000000") },
            { 730, BigInteger.Parse("5020233000000000000000000") },
            { 1095, BigInteger.Parse("70471335000000000000000000") },
            { 2190, BigInteger.Parse("400000000000000000000000000") }
        };

        // GET api/blockchain
        /// <summary>Creditcoin blockchain info</summary>
        /// <returns>A new Blockchain object</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet]
        [ProducesResponseType(200)]
        [ProducesResponseType(503)]
        public IActionResult Get()
        {
            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            Models.Block tip = Cache.Tip();

            var blockchain = new Blockchain
            {
                BlockHeight = tip.BlockNum,
                Difficulty = tip.Difficulty,
                BlockReward = calculateBlockReward(tip.BlockNum),
                TrnsactionFee = "10000000000000000", //TODO: remove, use only TransactionFee
                TransactionFee = "10000000000000000",
                CirculationSupply = Cache.calculateSupply().ToString(),
                NetworkWeight = calculateNetworkWeight(tip.Difficulty),
                CtcInCirculation = calculateCtcInCirculation().ToString()
            };
            return Json(blockchain);
        }

        private static string fromCredo(string credos)
        {
            string result;
            if (credos.Length > 18)
            {
                result = $"{credos.Substring(0, credos.Length - 18)}.{credos.Substring(credos.Length - 18)}";
            }
            else
            {
                result = $"0.{new String('0', 18 - credos.Length)}{credos}";
            }
            return result;
        }

        // GET api/blockchain/ctcInCirculation
        /// <summary>The amount of CTC (aka G-CRE) currently available for exchange</summary>
        /// <returns>The amount in CTC</returns>
        /// <response code="200">If succeeds</response>
        [HttpGet("ctcInCirculation")]
        [ProducesResponseType(200)]
        public IActionResult GetCtcInCirculation()
        {
            var amount = fromCredo(calculateCtcInCirculation().ToString());
            return Ok(amount);
        }

        // GET api/blockchain/creditcoinsInCirculation
        /// <summary>The amount of Creditcoins available on the network</summary>
        /// <returns>The amount in Creditcoins</returns>
        /// <response code="200">If succeeds</response>
        [HttpGet("creditcoinsInCirculation")]
        [ProducesResponseType(200)]
        public IActionResult GetCreditcoinsInCirculation()
        {
            var amount = fromCredo(Cache.calculateSupply().ToString());
            return Ok(amount);
        }

        // GET api/blockchain/circulatingSupply
        /// <summary>The amount of Creditcoins available on the network + vested CTC (aka G-CRE)</summary>
        /// <returns>The amount in Creditcoins</returns>
        /// <response code="200">If succeeds</response>
        [HttpGet("circulatingSupply")]
        [ProducesResponseType(200)]
        public IActionResult GetCreditcoinsReserve()
        {
            var amount = fromCredo((Cache.calculateSupply() + calculateCtcInCirculation()).ToString());
            return Ok(amount);
        }

        // GET api/blockchain/richList
        /// <summary>The amount of Creditcoins available on the network</summary>
        /// <returns>The amount in Creditcoins</returns>
        /// <response code="200">If succeeds</response>
        /// <response code="503">If unable to access Creditcoin network</response>
        [HttpGet("richList")]
        [ProducesResponseType(200)]
        [ProducesResponseType(503)]
        public IActionResult GetRichList()
        {
            if (!Cache.IsSuccessful())
                return new StatusCodeResult(503);

            var wallets = Cache.getWallets().ToList();
            wallets.Sort((pair1, pair2) => -pair1.Value.CompareTo(pair2.Value));
            var ret = wallets.Take(20).Select(pair => $"{pair.Key}: {fromCredo(pair.Value.ToString())}");
            return Json(ret);
        }

        private static BigInteger OLD_REWARD = BigInteger.Parse("222000000000000000000");
        private static BigInteger NEW_REWARD = BigInteger.Parse("28");
        private static BigInteger LAST_BLOCK_WITH_OLD_REWARD = BigInteger.Parse("279410");
        private const int BLOCKS_IN_PERIOD = 2500000;

        internal static string calculateBlockReward(string tipBlockNumStr)
        {
            var tipBlockNum = BigInteger.Parse(tipBlockNumStr);
            if (tipBlockNum > LAST_BLOCK_WITH_OLD_REWARD)
            {
                var period = (int)(tipBlockNum / BLOCKS_IN_PERIOD);
                var fraction = Math.Pow(19.0 / 20.0, period);
                var number = fraction.ToString("F18");
                var pos = number.IndexOf('.');
                var builder = new StringBuilder();
                if (number[0] != '0')
                {
                    builder.Append(number.Substring(0, pos));
                    builder.Append(number.Substring(pos + 1));
                }
                else
                {
                    pos = 2;
                    for (; number[pos] == '0'; ++pos);
                    builder.Append(number.Substring(pos));
                }
                var fractionStr = builder.ToString();
                var reward = NEW_REWARD * (BigInteger.Parse(fractionStr));
                return reward.ToString();
            }
            else
            {
                return OLD_REWARD.ToString();
            }
        }

        private static string calculateNetworkWeight(string difficultyStr)
        {
            var difficulty = int.Parse(difficultyStr);
            return Math.Pow(2, difficulty).ToString();
        }

        private static BigInteger calculateCtcInCirculation()
        {
            var vestingStart = new DateTime(2019, 4, 22);
            var now = DateTime.Now;
            var days = (now - vestingStart).Days - 1;
            BigInteger circulated = 0;
            foreach (var i in vesting)
            {
                if (days >= i.Key)
                    circulated += i.Value;
                else
                    circulated += i.Value / i.Key * days;
            }
            return circulated;
        }
    }
}
