using System;
using System.Net;

namespace Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy
{
    internal static class NextendoGgstRedirect
    {
        internal static bool IsServiceHost(string host, ulong programId)
        {
            // JP 1.08 is verified; the other regional IDs follow the Citron rule.
            bool isStrive = programId == 0x0100AB401F136000UL ||
                            programId == 0x01002F4020718000UL ||
                            programId == 0x01002F402071A000UL ||
                            programId == 0x010034A020A5C000UL;

            // EOS is shared with other games, so all four hosts remain title-scoped.
            return isStrive &&
                   (string.Equals(host, "api.epicgames.dev", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, "switch-ggst-game.guiltygear.com", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, "prod-switch-match-ggst-game.guiltygear.com", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(host, "prod-to3match-ggst-game.guiltygear.com", StringComparison.OrdinalIgnoreCase));
        }

        internal static IPAddress ResolveAddress(IPAddress configuredAddress)
        {
            string value = Environment.GetEnvironmentVariable("NEXTENDO_GGST_IP");
            IPAddress address;

            return IPAddress.TryParse(value, out address) ? address : configuredAddress;
        }
    }
}
