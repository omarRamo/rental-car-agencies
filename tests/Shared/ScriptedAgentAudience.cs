using CarRental.Application.Messaging.Agent.Turns;
using CarRental.Infrastructure.Messaging.Agent;

namespace CarRental.Testing;

/// <summary>
/// The audience of the WhatsApp agent on the test hosts: only the numbers a test scripted on the fake model
/// (<see cref="ScriptedChatClient.Script"/>). Every other conversation keeps the silent bot of package 4A, so that the
/// tests of the inbox, the notifications and the webhook count only the messages they send themselves.
/// </summary>
public sealed class ScriptedAgentAudience(ScriptedChatClient model) : IBotAudience
{
    public bool Includes(string whatsAppNumber) => model.HasScript(whatsAppNumber);
}
