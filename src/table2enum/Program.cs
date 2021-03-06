﻿using Microsoft.Extensions.CommandLineUtils;
using System;
using Humanizer;
using System.Linq;
using System.Text;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using EnsureThat;
using System.Xml.Linq;
using System.IO;

namespace table2enum
{
    public class Program
    {
        public static void Main(string[] args)
        {
            System.AppDomain.CurrentDomain.UnhandledException += HandlerError;

            // https://blog.terribledev.io/Parsing-cli-arguments-in-dotnet-core-Console-App/
            var app = new CommandLineApplication
            {
                Name = "table2num",
                Description = "Generate enum csharp or typescript from any SQL Server table"
            };
            app.HelpOption("-? | -h | --help");
            Gen(app);

            var result = app.Execute(args);
            Environment.Exit(result);
        }

        private static void HandlerError(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine(e.ExceptionObject.ToString());
            Environment.Exit(1);
        }

        private static void Gen(CommandLineApplication command)
        {
            var connectionStringOption = command.Option("-cs | --connection-string", "Connection string to connect SQL Server database.", CommandOptionType.SingleValue);
            var connectionStringConfigOption = command.Option("-cscf | --connection-string-config-file", "Connection string configuration file.", CommandOptionType.SingleValue);
            var tableOption = command.Option("-t | --table", "Table name.", CommandOptionType.SingleValue);
            var idOption = command.Option("-id | --identification-column", "ID column name.", CommandOptionType.SingleValue);
            var descriptionOption = command.Option("-d | --description-column", "Description column name.", CommandOptionType.SingleValue);
            var csharpOption = command.Option("-csf | --csharp-file", "C# export filename.", CommandOptionType.SingleValue);
            var typescriptOption = command.Option("-tsf | --typescript-file", "Typescript export filename.", CommandOptionType.SingleValue);
            var classNameOption = command.Option("-c | --class-name", "Class name of object.", CommandOptionType.SingleValue);
            var nameSpaceOption = command.Option("-ns | --name-space", "Namespace of object.", CommandOptionType.SingleValue);
            var importsOption = command.Option("-i | --imports", "Namespaces to import.", CommandOptionType.MultipleValue);
            var blackWordsOption = command.Option("-bw | --black-words", "Exclude black words.", CommandOptionType.MultipleValue);

            command.OnExecute(() =>
            {
                // Validation
                Ensure.Bool.IsTrue(tableOption.HasValue(), "-t | --table");
                Ensure.Bool.IsTrue(idOption.HasValue(), "-id | --identification-column");
                Ensure.Bool.IsTrue(descriptionOption.HasValue(), "-d | --description-column");
                Ensure.Bool.IsTrue(classNameOption.HasValue(), "-c | --class-name");
                Ensure.Bool.IsTrue(connectionStringOption.HasValue() || connectionStringConfigOption.HasValue(), "-cs | -cscf", opts => opts.WithMessage("You must specify connection string or XML config file"));
                Ensure.Bool.IsFalse(connectionStringOption.HasValue() && connectionStringConfigOption.HasValue(), "-cs | -cscf", opts => opts.WithMessage("You can't specify twice parameters together"));
                Ensure.Bool.IsTrue(csharpOption.HasValue() || typescriptOption.HasValue(), "-csf | -tsf", opts => opts.WithMessage("You must specify at once destiny file"));

                if (csharpOption.HasValue())
                {
                    Ensure.Bool.IsTrue(nameSpaceOption.HasValue(), "-ns | --name-space");
                    Ensure.Bool.IsTrue(importsOption.HasValue(), "-i | --imports");
                }

                var connectionString = GetConnectionString(connectionStringOption.Value(), connectionStringConfigOption.Value());

                // get database values
                var dataValues = Connection.List(connectionString, tableOption.Value(), idOption.Value(), descriptionOption.Value());

                // treatment
                List<dynamic> listing = new List<dynamic>();
                foreach (var item in dataValues)
                {
                    var field = RemoveBlackWords(RemoveDiacritics(item.Value), blackWordsOption.Values)
                        .ToLower()
                        .Underscore()
                        .Pascalize()
                        .Replace("_", string.Empty);

                    listing.Add(new
                    {
                        id = item.Key,
                        field = field,
                        description = item.Value
                    });
                };

                // generate csharp file
                if (csharpOption.HasValue())
                {
                    var code = GenereteEnumCsharp(nameSpaceOption.Value(), classNameOption.Value(), listing, importsOption.Values);
                    System.IO.File.WriteAllText(csharpOption.Value(), code);
                }

                // generate typescript file
                if (typescriptOption.HasValue())
                {
                    var code = GenereteEnumTypescript(classNameOption.Value(), listing);
                    System.IO.File.WriteAllText(typescriptOption.Value(), code);
                }

                return Environment.ExitCode;
            });
        }

        private static string GetConnectionString(string cs, string csConfig)
        {
            if (!string.IsNullOrEmpty(cs))
                return cs;

            var file = Path.GetFullPath(csConfig);
            XDocument doc = XDocument.Parse(System.IO.File.ReadAllText(file));
            return doc?
                    .Root?
                    .DescendantsAndSelf("connectionStrings")?
                    .FirstOrDefault()?
                    .Element("add")?
                    .Attribute("connectionString")?
                    .Value ?? throw new Exception("Connection string don't be found");
        }

        /// <summary>
        /// http://www.levibotelho.com/development/c-remove-diacritics-accents-from-a-string/
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            text = text.Normalize(NormalizationForm.FormD);
            var chars = text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
            return new string(chars).Normalize(NormalizationForm.FormC);
        }

        public static T[] JoinArray<T>(T[] firstArray, T[] secondArray)
        {
            T[] resultArray = new T[firstArray.Length + secondArray.Length];
            Array.Copy(firstArray, resultArray, firstArray.Length);
            Array.Copy(secondArray, 0, resultArray, firstArray.Length, secondArray.Length);

            return resultArray;
        }

        public static string RemoveBlackWords(string text, List<string> appendBlackWords)
        {
            var blackWords = JoinArray(new string[] { "da", "de", "di", "do", "du", "of", "the" }, appendBlackWords.ToArray())
                .ToList()
                .ConvertAll(i => i.ToLower());

            var words = text
                .Split(' ')
                .Where(o => !blackWords.Contains(o.ToLower()));

            return string.Join(' ', words);
        }

        public static string GenereteEnumCsharp(string namespaceText, string enumnameText, List<dynamic> fields, List<string> imports)
        {
            StringBuilder sb = new StringBuilder();
            foreach (var item in imports)
            {
                sb.AppendLine($"using {item};");
            }
            sb.AppendLine(string.Empty);
            sb.AppendLine($"namespace {namespaceText}");
            sb.AppendLine("{");

            sb.AppendLine($"public enum {enumnameText}");
            sb.AppendLine("{");

            var idx = 0;
            foreach (var item in fields)
            {
                idx++;
                if (!string.IsNullOrEmpty(item.description))
                {
                    sb.AppendLine($"[Description(\"{item.description}\")]");
                }

                if (idx < fields.Count)
                {
                    sb.AppendLine($"{item.field} = {item.id},");
                    sb.AppendLine(string.Empty);
                }
                else
                {
                    sb.AppendLine($"{item.field} = {item.id}");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine("}");

            var code = SyntaxFactory.ParseCompilationUnit(sb.ToString());
            return code
                .NormalizeWhitespace()
                .ToFullString();
        }

        public static string GenereteEnumTypescript(string enumnameText, List<dynamic> fields)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"export enum {enumnameText}");
            sb.AppendLine("{");

            var idx = 0;
            foreach (var item in fields)
            {
                string fi = item.field.ToString();
                fi = fi.Camelize();
                idx++;
                if (idx < fields.Count)
                {
                    sb.AppendLine($"    {fi} = {item.id},");
                }
                else
                {
                    sb.AppendLine($"    {fi} = {item.id}");
                }
            }

            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// https://carlos.mendible.com/2017/03/02/create-a-class-with-net-core-and-roslyn/ The
        /// roslyn generation will be implemented
        /// </summary>
        /// <param name="fields"></param>
        /// <returns></returns>
        public static string GenereteEnumCsharpRoslyn(List<dynamic> fields)
        {
            throw new NotImplementedException();

            // Create a namespace:
            var @namespace = SyntaxFactory.NamespaceDeclaration(
                SyntaxFactory.ParseName("Suframa.Cadsuf.CrossCutting.DataTransferObject.Enum")
            ).NormalizeWhitespace();

            // Add System using statement: (using System)
            @namespace = @namespace.AddUsings(
                SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName("System.ComponentModel")
                    ));

            // Create a class: (class Order)
            var classDeclaration = SyntaxFactory.EnumDeclaration("EnumTipoRequerimento");

            var generatedEnumDeclarationSyntax = classDeclaration.AddMembers(
                SyntaxFactory.EnumMemberDeclaration("Aston"),
                SyntaxFactory.EnumMemberDeclaration("Villa"));

            foreach (dynamic item in fields)
            {
                var member = SyntaxFactory.EnumMemberDeclaration(item.field + " = " + item.id);
                classDeclaration.AddMembers(member);
            }

            // Add the public modifier: (public class Order)
            classDeclaration = classDeclaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

            // Add the class to the namespace.
            @namespace = @namespace.AddMembers(classDeclaration);

            // Normalize and get code as string.
            var code = @namespace
                .NormalizeWhitespace()
                .ToFullString();

            return code;
        }
    }
}