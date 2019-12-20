using System;
using System.Numerics;
using Microsoft.AspNetCore.Mvc;
using ccbe.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ccbe.Controllers
{
    /// <summary>
    /// Blockchain entries
    /// </summary>
    [Route("api/[controller]")]
    [ApiController]
    public class BlockchainController : Controller
    {
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
                TrnsactionFee = "10000000000000000",
                CirculationSupply = Cache.calculateSupply(),
                NetworkWeight = calculateNetworkWeight(tip.Difficulty)
            };
            return Json(blockchain);
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
    }
}