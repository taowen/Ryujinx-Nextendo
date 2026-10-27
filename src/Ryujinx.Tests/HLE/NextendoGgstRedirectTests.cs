using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy;
using System;
using System.Net;

namespace Ryujinx.Tests.HLE
{
    [NonParallelizable]
    public class NextendoGgstRedirectTests
    {
        [Test]
        public void ExactHostsAreScopedToStriveTitles()
        {
            ulong[] titles = { 0x0100AB401F136000UL, 0x01002F4020718000UL, 0x01002F402071A000UL, 0x010034A020A5C000UL };
            string[] hosts =
            {
                "api.epicgames.dev",
                "switch-ggst-game.guiltygear.com",
                "prod-switch-match-ggst-game.guiltygear.com",
                "prod-to3match-ggst-game.guiltygear.com",
            };

            foreach (ulong title in titles)
            {
                foreach (string host in hosts)
                {
                    Assert.That(NextendoGgstRedirect.IsServiceHost(host, title), Is.True);
                    Assert.That(NextendoGgstRedirect.IsServiceHost(host.ToUpperInvariant(), title), Is.True);
                }

                Assert.That(NextendoGgstRedirect.IsServiceHost("api.epicgames.dev.example.org", title), Is.False);
                Assert.That(NextendoGgstRedirect.IsServiceHost(null, title), Is.False);
            }

            foreach (string host in hosts)
            {
                Assert.That(NextendoGgstRedirect.IsServiceHost(host, 0), Is.False);
                Assert.That(NextendoGgstRedirect.IsServiceHost(host, 0x0100B0C013912000UL), Is.False); // Among Us also uses EOS.
            }
        }

        [Test]
        public void DedicatedOverrideFallsBackToConfiguredAddress()
        {
            const string variable = "NEXTENDO_GGST_IP";
            string previous = Environment.GetEnvironmentVariable(variable);
            IPAddress configured = IPAddress.Parse("192.0.2.10");

            try
            {
                foreach (string unsetOrInvalid in new[] { null, "", "not-an-ip" })
                {
                    Environment.SetEnvironmentVariable(variable, unsetOrInvalid);
                    Assert.That(NextendoGgstRedirect.ResolveAddress(configured), Is.EqualTo(configured));
                }

                Environment.SetEnvironmentVariable(variable, "127.0.0.9");
                Assert.That(NextendoGgstRedirect.ResolveAddress(configured), Is.EqualTo(IPAddress.Parse("127.0.0.9")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
    }
}
