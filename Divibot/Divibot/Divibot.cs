﻿using Divibot.Attack;
using Divibot.Commands;
using Divibot.Database;
using Divibot.Database.Entities;
using Divibot.Music;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using DSharpPlus.Interactivity;
using DSharpPlus.Interactivity.Extensions;
using DSharpPlus.Lavalink;
using DSharpPlus.Net;
using DSharpPlus.SlashCommands;
using DSharpPlus.SlashCommands.Attributes;
using DSharpPlus.SlashCommands.EventArgs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace Divibot {

    public static class Divibot {

        // Settings
        public static EventId SlashCommandLogEventId { get; } = new EventId(1751, "SlashCommands");
        public static string Prefix { get; } = Environment.GetEnvironmentVariable("Prefix");
        public static Stopwatch Uptime { get; } = new Stopwatch();

        // Properties
        public static ServiceProvider Services { get; private set; }

        // Main
        public static async Task Main(string[] args) {
            // Dependency injection
            Services = new ServiceCollection()
                .AddSingleton(impl => {
                    return new DiscordClient(new DiscordConfiguration() {
                        Token = Environment.GetEnvironmentVariable("BotToken"),
                        Intents = DiscordIntents.Guilds |
                                  DiscordIntents.GuildMembers |
                                  DiscordIntents.GuildPresences |
                                  DiscordIntents.GuildMessages |
                                  DiscordIntents.GuildVoiceStates
                    });
                })
                .AddSingleton<Random>()
                .AddDbContext<DivibotDbContext>(ServiceLifetime.Transient)
                .AddSingleton<AttackService>()
                .AddSingleton<MusicService>()
                .BuildServiceProvider();

            // Create client
            DiscordClient client = Services.GetRequiredService<DiscordClient>();

            // Enable interactivity
            client.UseInteractivity(new InteractivityConfiguration() {
                Timeout = TimeSpan.FromMinutes(5)
            });

            // Handle ready event
            client.Ready += OnReady;

            // Handle message created event
            client.MessageCreated += OnMessageCreated;

            // Add slash commands
            SlashCommandsExtension commands = client.UseSlashCommands(new SlashCommandsConfiguration() {
                Services = Services
            });

            // Register modules
            ulong debugGuild = ulong.Parse(Environment.GetEnvironmentVariable("DebugGuild"));
            await RegisterCommandModules(commands, debugGuild);
#if DEBUG
            if (ulong.TryParse(Environment.GetEnvironmentVariable("DebugGuild2"), out ulong debugGuild2)) {
                await RegisterCommandModules(commands, debugGuild2);
            }
#endif
            commands.RegisterCommands<OwnerModule>(debugGuild);

            // Handle slash command executed
            commands.SlashCommandExecuted += OnSlashCommandExecuted;

            // Handle errored slash commands
            commands.SlashCommandErrored += OnSlashCommandErrored;

            // Create LavaLink
            LavalinkExtension lavalink = client.UseLavalink();

            // Start uptime
            Uptime.Start();

            // Connect
            await client.ConnectAsync();

            // Connect LavaLink
            await lavalink.ConnectAsync(new LavalinkConfiguration() {
                Password = Environment.GetEnvironmentVariable("LavalinkPassword"),
                RestEndpoint = new ConnectionEndpoint() {
                    Hostname = "127.0.0.1",
                    Port = 2333
                },
                SocketEndpoint = new ConnectionEndpoint() {
                    Hostname = "127.0.0.1",
                    Port = 2333
                }
            });

            // Create database
            DivibotDbContext dbContext = Services.GetRequiredService<DivibotDbContext>();

            // Apply migrations
            await dbContext.Database.MigrateAsync();

            // Update bot version
            await dbContext.UpdateBotVersionAsync();

            // Begin database time checks
            Timer databaseTimeCheckTimer = new Timer(TimeSpan.FromMinutes(1).TotalMilliseconds);
            databaseTimeCheckTimer.Elapsed += (o, e) => dbContext.PerformTimedChecksAsync();
            databaseTimeCheckTimer.Enabled = true;

            // Perform timed checks on launch
            dbContext.PerformTimedChecksAsync();

            // Don't close immediately
            await Task.Delay(-1);
        }
        
        // Registers the command modules
        private static async Task RegisterCommandModules(SlashCommandsExtension commands, ulong guildId) {
#if DEBUG
            commands.RegisterCommands<GeneralModule>(guildId);
            commands.RegisterCommands<InfoModule>(guildId);
            commands.RegisterCommands<EncodeModule>(guildId);
            commands.RegisterCommands<DecodeModule>(guildId);
            commands.RegisterCommands<AttackModule>(guildId);
            commands.RegisterCommands<ModerationModule>(guildId);
            commands.RegisterCommands<MusicModule>(guildId);
#else
            commands.RegisterCommands<GeneralModule>();
            commands.RegisterCommands<InfoModule>();
            commands.RegisterCommands<EncodeModule>();
            commands.RegisterCommands<DecodeModule>();
            commands.RegisterCommands<AttackModule>();
            commands.RegisterCommands<ModerationModule>();
            commands.RegisterCommands<MusicModule>();
#endif
        }

        // Handle bot ready event
        private static async Task OnReady(DiscordClient client, ReadyEventArgs evt) {
            // Set activity
            await client.UpdateStatusAsync(new DiscordActivity() {
                ActivityType = ActivityType.Playing,
                Name = "with slash commands"
            });
        }

        // Handle messages being recieved
        private static async Task OnMessageCreated(DiscordClient client, MessageCreateEventArgs evt) {
            DivibotDbContext dbContext = Services.GetRequiredService<DivibotDbContext>();

            // Check if author is AFK
            EntityAfkUser afkAuthor = dbContext.AfkUsers.SingleOrDefault(u => u.UserId == evt.Author.Id);
            if (afkAuthor != null) {
                // Remove from database
                dbContext.AfkUsers.Remove(afkAuthor);
                await dbContext.SaveChangesAsync();

                // Respond
                await evt.Channel.SendMessageAsync("Welcome back! I've removed your AFK message :thumbsup:");
            }

            // Check for any AFK mentions
            foreach (DiscordUser user in evt.MentionedUsers) {
                if (user.Id == evt.Author.Id) {
                    continue;
                }

                EntityAfkUser afkUser = dbContext.AfkUsers.SingleOrDefault(u => u.UserId == user.Id);
                if (afkUser != null) {
                    await evt.Channel.SendMessageAsync($":small_orange_diamond: **{user.Username}** is currently AFK! Reason: *{afkUser.Message}*");
                }
            }
        }

        // Handle slash command execution
        private static async Task OnSlashCommandExecuted(SlashCommandsExtension ext, SlashCommandExecutedEventArgs evt) {
            string executedBy = $"{evt.Context.User.Username}#{evt.Context.User.Discriminator} ({evt.Context.User.Id})";
            string executedIn;
            if (evt.Context.Guild != null) {
                executedIn = $"{evt.Context.Guild.Name} ({evt.Context.Guild.Id})";
            } else {
                executedIn = "DMs";
            }
            ext.Client.Logger.LogInformation(SlashCommandLogEventId, $"Slash command /{evt.Context.CommandName} was executed by {executedBy} in {executedIn}");
        }

        // Handle slash command errors
        private static async Task OnSlashCommandErrored(SlashCommandsExtension ext, SlashCommandErrorEventArgs evt) {
            string content = "";

            if (evt.Exception is SlashExecutionChecksFailedException) {
                IReadOnlyList<SlashCheckBaseAttribute> failedChecks = (evt.Exception as SlashExecutionChecksFailedException).FailedChecks;
                bool found = false;
                foreach (SlashCheckBaseAttribute check in failedChecks) {
                    if (check is SlashRequireGuildAttribute) {
                        content = "Sorry, but this command only works inside of servers.";
                        found = true;
                    } else if (check is SlashRequireDirectMessageAttribute) {
                        content = "Sorry, but this command only works inside of DMs.";
                        found = true;
                    } else if (check is SlashRequireUserPermissionsAttribute) {
                        SlashRequireUserPermissionsAttribute userPermAttribute = check as SlashRequireUserPermissionsAttribute;
                        if (evt.Context.Member != null && evt.Context.Member.Permissions.HasPermission(Permissions.ManageRoles)) {
                            content = $"Sorry, but you do not have enough permissions to run this command. You're missing one of the following: {userPermAttribute.Permissions.ToPermissionString()}";
                        } else {
                            content = $"Sorry, but you do not have enough permissions to run this command.";
                        }
                        found = true;
                    } else if (check is SlashRequireBotPermissionsAttribute) {
                        SlashRequireBotPermissionsAttribute botPermAttribute = check as SlashRequireBotPermissionsAttribute;
                        if (evt.Context.Member != null && evt.Context.Member.Permissions.HasPermission(Permissions.ManageRoles)) {
                            content = $"It seems as though I don't have enough permissions to run this command. I'm missing one of the following: {botPermAttribute.Permissions.ToPermissionString()}";
                        } else {
                            content = $"It seems as though I don't have enough permissions to run this command.";
                        }
                        found = true;
                    } else if (check is SlashRequireOwnerAttribute) {
                        content = $"Sorry, only the owner of the bot can run this command.";
                        found = true;
                    }
                    if (found) {
                        break;
                    }
                }
                if (!found) {
                    // Handle unknown checks
                    content = $"There was an internal error trying to run this command. Please try again, or if the error continues occuring, contact the bot developer with the following information:\n```\n" +
                              $"A check failed, but I'm not quite sure which one. See failed checks {string.Join(",", failedChecks.Select(c => c.GetType().Name).ToArray())}\n```";
                }
            } else {
                content = $"There was an internal error trying to run this command. Please try again, or if the error continues occuring, contact the bot developer with the following information:\n```\n{evt.Exception.Message}\n```";
            }

            // Respond
            try {
                await evt.Context.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, new DiscordInteractionResponseBuilder() {
                    Content = content,
                    IsEphemeral = true
                });
            } catch (Exception) {
                try {
                    // Presume it was acknowledged already
                    await evt.Context.EditResponseAsync(new DiscordWebhookBuilder() {
                        Content = content
                    });
                } catch (Exception) {
                    // Oh well, we'll log it.
                }
            }

            // Don't log error if it's just a failed check.
            if (!(evt.Exception is SlashExecutionChecksFailedException)) {
                // Log error
                evt.Context.Client.Logger.LogError(SlashCommandLogEventId, evt.Exception, evt.Exception.Message);

                // Add extra details for bad requests
                if (evt.Exception is BadRequestException) {
                    evt.Context.Client.Logger.LogError(SlashCommandLogEventId, (evt.Exception as BadRequestException).Errors);
                }
            }
        }

        // Divibot's definitely exclusive and totally amazing pagination feature
        public static string Pagination(string[] lines, int page = 0) {
            int pages = (int) Math.Ceiling((double) lines.Length / 10);
            string output = "";
            if (pages == 1) {
                foreach (string line in lines) {
                    output += line + '\n';
                }
            } else if (pages > 1) {
                if (page == 0) {
                    for (int i = 0; i < 10; i++) {
                        output += lines[i] + '\n';
                    }
                } else if (page == pages - 1) {
                    for (int i = page * 10; i < lines.Length; i++) {
                        output += lines[i] + '\n';
                    }
                } else {
                    for (int i = page * 10; i < (page * 10) + 10; i++) {
                        output += lines[i] + '\n';
                    }
                }
            }
            return output;
        }

        // Divibot's not-as-amazing-as-pagination-but-still-totally amazing comma-separated list creation feature
        public static string CreateCommaList(List<string> items, string combiningWord) {
            if (items.Count == 0) {
                return "N/A";
            } else if (items.Count == 1) {
                return items.First();
            } else if (items.Count == 2) {
                return $"{items.First()} {combiningWord} {items.Last()}";
            } else {
                return string.Join(", ", items.ToArray(), 0, items.Count - 1) + " " + combiningWord + " " + items.Last();
            }
        }

        // Converts the given string input to snake case.
        public static string ToSnakeCase(string input) {
            return input.ToUpper().Replace(" ", "_");
        }

        // Converts the given string input (assuming it is in snake case first) to proper case
        public static string ToProperCase(string input) {
            string[] parts = input.ToLower().Split("_");
            for (int i = 0; i < parts.Length; i++) {
                parts[i] = parts[i].Substring(0, 1).ToUpper() + parts[i].Substring(1, parts[i].Length - 1);
            }
            return string.Join(" ", parts);
        }

        // List shuffle extension method
        public static void Shuffle<T>(this IList<T> list) {
            int n = list.Count;
            while (n > 1) {
                n--;
                int k = Services.GetService<Random>().Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

    }

}
