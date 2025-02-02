﻿// Copyright 2021 Flavien Charlon
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Quickwire.Attributes;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Specifies that a parameter or property should be bound using a configuration setting coming from the
/// <see cref="IConfiguration"/> service.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class InjectConfigurationAttribute : Attribute, IDependencyResolver
{
    private static readonly MethodInfo _asReadOnlyMethodInfo = typeof(Array).GetMethod(nameof(Array.AsReadOnly))!;

    public InjectConfigurationAttribute(string configurationKey)
    {
        ConfigurationKey = configurationKey;
    }

    /// <summary>
    /// Gets or sets the configuration key to use.
    /// </summary>
    public string ConfigurationKey { get; set; }

    public object? Resolve(IServiceProvider serviceProvider, Type type)
    {
        IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();
        string value = configuration[ConfigurationKey];

        if (type.IsGenericType)
        {
            if (ImplementsGenericType(type, typeof(IReadOnlyList<>), out Type typeParameter))
                return CreateTypedReadOnlyList(serviceProvider, typeParameter, configuration);
            else if (ImplementsGenericType(type, typeof(IList<>), out typeParameter))
                return CreateTypedArray(serviceProvider, typeParameter, configuration);
        }

        if (IsList(type))
        {
            return CreateTypedList(serviceProvider, type, configuration);
        }
        if (type.IsArray)
        {
            return CreateTypedArray(serviceProvider, type.GetElementType()!, configuration);
        }

        return ResolveValue(type, value, serviceProvider);
    }
    private bool IsList(Type type)=>type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>);
    private object CreateTypedList(IServiceProvider serviceProvider, Type type,
        IConfiguration configuration)
    {
        List<string> children = configuration
            .GetSection(ConfigurationKey)
            .GetChildren()
            .Select(child => child.Value)
            .ToList();
        object? result=Activator.CreateInstance(type);
        MethodInfo? addRangeMethod = type.GetMethod("Add");
        for (int i = 0; i < children.Count; i++)
            addRangeMethod.Invoke(result, new[] { ResolveValue(type.GetGenericArguments()[0], children[i], serviceProvider) });
        return result;
    }
    private static object? ResolveValue(Type type, string value, IServiceProvider serviceProvider)
    {
        if (type == typeof(string))
            return value;

        Type? nullableOf = Nullable.GetUnderlyingType(type);

        if (nullableOf != null)
            return value == null ? null : ResolveValue(nullableOf, value, serviceProvider);
        else if (type == typeof(TimeSpan))
            return TimeSpan.Parse(value);
        else if (type == typeof(Guid))
            return Guid.Parse(value);
        else if (type == typeof(Uri))
            return new Uri(value);
        else if (type.IsEnum)
            return Enum.Parse(type, value);
        else
        {
            try
            {
                return Convert.ChangeType(value, type);
            }
            catch (InvalidCastException)
            {
                MethodInfo? parse = type.GetMethod(
                    "Parse",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(string) },
                    Array.Empty<ParameterModifier>());
                try
                {
                    if (parse != null && !parse.IsGenericMethod)
                        return parse.Invoke(null, new object[] { value });
                    else
                        throw new InvalidCastException();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"can not convert value {value} to type ${type}, return default value of ${type}");
                    return default;
                }

            }
        }
    }

    private static bool ImplementsGenericType(Type type, Type openType, out Type typeParameter)
    {
        foreach (Type implementedInterface in openType.GetInterfaces().Append(openType))
        {
            if (implementedInterface.IsGenericType &&
                type.GetGenericTypeDefinition() == implementedInterface.GetGenericTypeDefinition())
            {
                typeParameter = type.GenericTypeArguments[0];
                return true;
            }
        }

        typeParameter = null!;
        return false;
    }

    private object CreateTypedArray(IServiceProvider serviceProvider, Type type, IConfiguration configuration)
    {
        List<string> children = configuration
            .GetSection(ConfigurationKey)
            .GetChildren()
            .Select(child => child.Value)
            .ToList();

        Array result = Array.CreateInstance(type, children.Count);

        for (int i = 0; i < result.Length; i++)
            result.SetValue(ResolveValue(type, children[i], serviceProvider), i);

        return result;
    }

    private object? CreateTypedReadOnlyList(IServiceProvider serviceProvider, Type type, IConfiguration configuration)
    {
        object array = CreateTypedArray(serviceProvider, type, configuration);

        return _asReadOnlyMethodInfo.MakeGenericMethod(type).Invoke(null, new[] { array });
    }
}

