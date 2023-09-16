﻿using System.Data;
using System.Text;

namespace TSBindDotNet;

public static class SourceCodeParser
{
    public static string StripReturnTypeName(string typeName)
    {
        typeName = StripGenericType(typeName, "Task");
        typeName = StripGenericType(typeName, "ActionResult");

        return typeName;
    }

    public static string StripGenericType(string str, string genericTypeName)
    {
        var indexOf = str.IndexOf($"{genericTypeName}<");

        if (indexOf == -1)
            return str;

        str = string.Concat(str.AsSpan(0, indexOf), str.AsSpan(indexOf + $"{genericTypeName}<".Length));
        str = str[..str.LastIndexOf(">")];

        return str;
    }

    public static SourceFileClass? FindClass(List<ProjectInput> projectInputs, string name)
    {
        foreach (var projectInput in projectInputs)
        {
            var sourceClass = projectInput.SourceClasses.Where(c => c.Name == name).FirstOrDefault();
            if (sourceClass != null)
                return sourceClass;
        }

        return null;
    }

    public static SourceFileEnum? FindEnum(List<ProjectInput> projectInputs, string name)
    {
        foreach (var projectInput in projectInputs)
        {
            var sourceEnum = projectInput.SourceEnums.Where(c => c.Name == name).FirstOrDefault();
            if (sourceEnum != null)
                return sourceEnum;
        }

        return null;
    }

    public static void FindReferenceTypes(SourceFileClass sourceClass, List<string> referenceTypeNames)
    {
        foreach (var field in sourceClass.Fields)
            referenceTypeNames.AddIfNotContains(field.TypeName);

        foreach (var property in sourceClass.Properties)
            referenceTypeNames.AddIfNotContains(property.TypeName);
    }

    public static string TransformTypeName(string typeName)
    {
        typeName = typeName.Replace("List<", "Array<");
        typeName = typeName.Replace("int", "number");
        typeName = typeName.Replace("float", "number");
        typeName = typeName.Replace("decimal", "number");
        typeName = typeName.Replace("bool", "boolean");

        if (typeName.EndsWith("?"))
            typeName = typeName.Substring(0, typeName.Length - 1);

        return typeName;
    }

    public static StringBuilder GenerateTS(
        List<string> inputs,
        List<string> includeTypes,
        string generalTemplatePath,
        string apiControllerTemplatePath,
        string apiEndpointTemplatePath)
    {
        var output = new StringBuilder();
        var projectInputs = new List<ProjectInput>();

        output.AppendLine("/* This file was automatically generated by https://github.com/pandepic/TSBind.Net */");
        output.AppendLine();

        var generalTemplate = string.IsNullOrEmpty(generalTemplatePath) ? "" : File.ReadAllText(generalTemplatePath);
        var apiControllerTemplate = File.ReadAllText(apiControllerTemplatePath);
        var apiFunctionTemplate = File.ReadAllText(apiEndpointTemplatePath);

        var referenceTypeNames = new List<string>();
        referenceTypeNames.AddRange(includeTypes);

        output.AppendLine(generalTemplate);
        output.AppendLine();

        foreach (var input in inputs)
            projectInputs.Add(new(input));

        var apiControllers = new List<SourceFileClass>();

        foreach (var projectInput in projectInputs)
        {
            apiControllers.AddRange(projectInput.SourceClasses
                .Where(c => c.Attributes.Where(a => a.Name == AttributeNames.ApiController.ToString()).Any()));
        }

        foreach (var controller in apiControllers)
        {
            foreach (var method in controller.ClassMethods)
            {
                referenceTypeNames.AddIfNotContains(StripReturnTypeName(method.ReturnTypeName));

                foreach (var paramType in method.ParameterTypes)
                    referenceTypeNames.AddIfNotContains(paramType);
            }
        }

        var addReferenceTypes = new List<string>();

        foreach (var referenceType in referenceTypeNames)
        {
            var sourceClass = FindClass(projectInputs, referenceType);
            if (sourceClass != null)
                FindReferenceTypes(sourceClass, addReferenceTypes);
        }

        referenceTypeNames.AddRangeIfNotContains(addReferenceTypes);

        for (var i = referenceTypeNames.Count - 1; i >= 0; i--)
        {
            var referenceType = referenceTypeNames[i];
            var classType = FindClass(projectInputs, referenceType);
            var enumType = FindEnum(projectInputs, referenceType);

            if (classType == null && enumType == null)
                referenceTypeNames.RemoveAt(i);
        }

        #region Enums
        foreach (var referenceType in referenceTypeNames)
        {
            var enumType = FindEnum(projectInputs, referenceType);

            if (enumType == null)
                continue;

            output.AppendLine($"export enum {enumType.Name} {{");

            foreach (var member in enumType.Members)
                output.AppendLine($"\t{member.Name} = {member.Value},");

            output.AppendLine("}");

            if (referenceType != referenceTypeNames.Last())
                output.AppendLine();
        }

        output.AppendLine();
        #endregion

        #region Classes
        foreach (var referenceType in referenceTypeNames)
        {
            var classType = FindClass(projectInputs, referenceType);
            if (classType == null)
                continue;

            output.AppendLine($"export class {classType.Name} {{");

            foreach (var property in classType.Properties)
                output.AppendLine($"\tpublic {property.Name}?: {TransformTypeName(property.TypeName)};");

            output.AppendLine("}");

            if (referenceType != referenceTypeNames.Last())
                output.AppendLine();
        }
        #endregion

        output.AppendLine();

        foreach (var controller in apiControllers)
        {
            var controllerShortName = controller.Name;
            if (controllerShortName.EndsWith("Controller"))
                controllerShortName = controllerShortName.Substring(0, controllerShortName.LastIndexOf("Controller"));

            var controllerRoute = controller.Attributes.Where(a => a.Name == AttributeNames.Route.ToString()).FirstOrDefault();
            if (controllerRoute == null || controllerRoute.Arguments.Count == 0)
                continue;

            var controllerRouteString = controllerRoute.Arguments[0].Value.Replace("\"", "");
            controllerRouteString = controllerRouteString.Replace("[controller]", controllerShortName);

            var functionsOutput = new StringBuilder();

            foreach (var method in controller.ClassMethods)
            {
                if (method.ParameterTypes.Count == 0)
                    continue;

                var route = method.Attributes.Where(a => a.Name == AttributeNames.Route.ToString()).FirstOrDefault();
                if (route == null || route.Arguments.Count == 0)
                    continue;

                var routeString = controllerRouteString + "/" + route.Arguments[0].Value.Replace("\"", "");

                functionsOutput.AppendLine(apiFunctionTemplate
                    .Replace("{ENDPOINT_NAME}", method.Name)
                    .Replace("{ENDPOINT_ROUTE}", routeString)
                    .Replace("{ENDPOINT_PARAM_TYPE_NAME}", method.ParameterTypes[0])
                    .Replace("{ENDPOINT_RESPONSE_TYPE_NAME}", StripReturnTypeName(method.ReturnTypeName)));

                if (method != controller.ClassMethods.Last())
                    functionsOutput.AppendLine();
            }

            var functionsString = functionsOutput.ToString();

            // remove extra newline at the end
            if (functionsString.EndsWith(Environment.NewLine))
                functionsString = functionsString.Substring(0, functionsString.Length - Environment.NewLine.Length);

            output.AppendLine(apiControllerTemplate
                .Replace("{CONTROLLER_SHORT_NAME}", controllerShortName)
                .Replace("{API_FUNCTIONS}", functionsString));

            if (controller != apiControllers.Last())
                output.AppendLine();
        }

        return output;
    }
}
