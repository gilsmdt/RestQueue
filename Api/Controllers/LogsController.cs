using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [Route("api/[controller]")]
    public class LogsController : Controller
    {
        private static int counter = 0;

        static LogsController()
        {
            Task.Run(() =>
            {
                while (true)
                {
                    Trace.WriteLine("server count " + counter); Thread.Sleep(1000);
                }
            });
        }

        // POST api/Logs
        [HttpPost]
        public async Task<IActionResult> Post(Log log)
        {
            Interlocked.Increment(ref counter);

            if (counter % 1000 == 0)
            {
                Trace.WriteLine(counter);
                Trace.WriteLine($"{log?.Timestamp} {log?.Message}");
            }

            // simulate server error every 50 requests
            if (counter % 50 == 0)
            {
                return StatusCode(500);
            }

            // simulate timeout every 70 requests
            if (counter % 70 == 0)
            {
                // timeout on the client side is set to 10s
                await Task.Delay(11 * 1000);
            }

            return StatusCode(200);
        }
    }
}
