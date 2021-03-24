// Copyright 2005-2015 Giacomo Stelluti Scala & Contributors. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine.Infrastructure;
using CSharpx;
using RailwaySharp.ErrorHandling;
using System.Text.RegularExpressions;

namespace CommandLine.Core
{
    static class Tokenizer
    {
        public static Result<IEnumerable<Token>, Error> Tokenize(
            IEnumerable<string> arguments,
            Func<string, NameLookupResult> nameLookup)
        {
            return Tokenizer.Tokenize(arguments, nameLookup, ignoreUnknownArguments:false, allowDashDash:true);
        }

        public static Result<IEnumerable<Token>, Error> Tokenize(
            IEnumerable<string> arguments,
            Func<string, NameLookupResult> nameLookup,
            bool ignoreUnknownArguments,
            bool allowDashDash)
        {
            var errors = new List<Error>();
            Action<string> onBadFormatToken = arg => errors.Add(new BadFormatTokenError(arg));
            Action<string> unknownOptionError = name => errors.Add(new UnknownOptionError(name));
            Action<string> doNothing = name => {};
            Action<string> onUnknownOption = ignoreUnknownArguments ? doNothing : unknownOptionError;

            int consumeNext = 0;
            var tokens = new List<Token>();
            Action<string> addValue = (s => tokens.Add(new Value(s)));
            Action<string> addName = (s => tokens.Add(new Name(s)));

            var enumerator = arguments.GetEnumerator();
            while (enumerator.MoveNext())
            {
                switch (enumerator.Current) {
                    case null:
                        break;

                    case string arg when consumeNext > 0:
                        addValue(arg);
                        consumeNext = consumeNext - 1;
                        break;

                    case "--" when allowDashDash:
                        consumeNext = System.Int32.MaxValue;
                        break;

                    case "--":
                        addValue("--");
                        break;

                    case "-":
                        // A single hyphen is always a value (it usually means "read from stdin" or "write to stdout")
                        addValue("-");
                        break;

                    case string arg when arg.StartsWith("--") && arg.Contains("="):
                        string[] parts = arg.Substring(2).Split(new char[] { '=' }, 2);
                        if (String.IsNullOrWhiteSpace(parts[0]) || parts[0].Contains(" "))
                        {
                            onBadFormatToken(arg);
                        }
                        else
                        {
                            switch(nameLookup(parts[0]))
                            {
                                case NameLookupResult.NoOptionFound:
                                    onUnknownOption(parts[0]);
                                    break;

                                default:
                                    addName(parts[0]);
                                    addValue(parts[1]);
                                    break;
                            }
                        }
                        break;

                    case string arg when arg.StartsWith("--"):
                        var name = arg.Substring(2);
                        switch (nameLookup(name))
                        {
                            case NameLookupResult.OtherOptionFound:
                                addName(name);
                                consumeNext = 1;
                                break;

                            case NameLookupResult.NoOptionFound:
                                // When ignoreUnknownArguments is true and AutoHelp is true, calling code is responsible for
                                // setting up nameLookup so that it will return a known name for --help, so that we don't skip it here
                                onUnknownOption(name);
                                break;

                            default:
                                addName(name);
                                break;
                        }
                        break;

                    case string arg when arg.StartsWith("-"):
                        // First option char that requires a value means we swallow the rest of the string as the value
                        // But if there is no rest of the string, then instead we swallow the next argument
                        string chars = arg.Substring(1);
                        int len = chars.Length;
                        if (len > 0 && Char.IsDigit(chars[0]))
                        {
                            // Assume it's a negative number
                            addValue(arg);
                            continue;
                        }
                        for (int i = 0; i < len; i++)
                        {
                            var s = new String(chars[i], 1);
                            switch(nameLookup(s))
                            {
                                case NameLookupResult.OtherOptionFound:
                                    addName(s);
                                    if (i+1 < len)
                                    {
                                        addValue(chars.Substring(i+1));
                                        i = len;  // Can't use "break" inside a switch, so this breaks out of the loop
                                    }
                                    else
                                    {
                                        consumeNext = 1;
                                    }
                                    break;

                                case NameLookupResult.NoOptionFound:
                                    onUnknownOption(s);
                                    break;

                                default:
                                    addName(s);
                                    break;
                            }
                        }
                        break;

                    case string arg:
                        // If we get this far, it's a plain value
                        addValue(arg);
                        break;
                }
            }

            return Result.Succeed<IEnumerable<Token>, Error>(tokens.AsEnumerable(), errors.AsEnumerable());
        }

        public static Result<IEnumerable<Token>, Error> ExplodeOptionList(
            Result<IEnumerable<Token>, Error> tokenizerResult,
            Func<string, Maybe<char>> optionSequenceWithSeparatorLookup)
        {
            // TODO: I don't like how this works. I don't want "-s foo;bar baz" to put three values into -s. Let's instead have a third token type, List, besides Name and Value.
            var tokens = tokenizerResult.SucceededWith().Memoize();

            var exploded = new List<Token>(tokens is ICollection<Token> coll ? coll.Count : tokens.Count());
            var nothing = Maybe.Nothing<char>();  // Re-use same Nothing instance for efficiency
            var separator = nothing;
            foreach (var token in tokens) {
                if (token.IsName()) {
                    separator = optionSequenceWithSeparatorLookup(token.Text);
                    exploded.Add(token);
                } else {
                    // Forced values are never considered option values, so they should not be split
                    if (separator.MatchJust(out char sep) && sep != '\0' && !token.IsValueForced()) {
                        if (token.Text.Contains(sep)) {
                            exploded.AddRange(token.Text.Split(sep).Select(Token.ValueFromSeparator));
                        } else {
                            exploded.Add(token);
                        }
                    } else {
                        exploded.Add(token);
                    }
                    separator = nothing;  // Only first value after a separator can possibly be split
                }
            }
            return Result.Succeed(exploded as IEnumerable<Token>, tokenizerResult.SuccessMessages());
        }

        public static Func<
                    IEnumerable<string>,
                    IEnumerable<OptionSpecification>,
                    Result<IEnumerable<Token>, Error>>
            ConfigureTokenizer(
                    StringComparer nameComparer,
                    bool ignoreUnknownArguments,
                    bool enableDashDash)
        {
            return (arguments, optionSpecs) =>
                {
                    var tokens = Tokenizer.Tokenize(arguments, name => NameLookup.Contains(name, optionSpecs, nameComparer), ignoreUnknownArguments, enableDashDash);
                    var explodedTokens = Tokenizer.ExplodeOptionList(tokens, name => NameLookup.HavingSeparator(name, optionSpecs, nameComparer));
                    return explodedTokens;
                };
        }
    }
}
