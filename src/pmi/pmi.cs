// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;

// This set of classes provides a way to forcibly jit a large number of methods.
// It can be used as is or included as a component in jit measurement and testing
// tools.
//
// In .Net Core, PrepareMethod should give codegen that is very similar to
// the code one would see if the method were actually called (the same is not
// as true in .Net Framework -- in particular the jit may make very different
// inlining decisions).
//
// Assemblies defining generic types and generic methods require special handling.
// Methods in generic types and generic methods can inspire the jit to create
// numerous different method bodies depending on the type parameters used
// for instantation.
// 
// The code below uses a very simple generic instantiation strategy. It currently
// only handles one- and two-parameter generic types with simple constraints.

// Base class for visiting types and methods in an assembly.
class Visitor
{
    protected DateTime startTime;
    protected string assemblyName;

    public virtual void Start()
    {

    }

    public virtual void Finish()
    {

    }

    public virtual void StartAssembly(Assembly assembly)
    {
        startTime = DateTime.Now;
        assemblyName = assembly.GetName().Name;
    }

    public virtual void FinishAssembly(Assembly assembly)
    {
    }

    public virtual void UninstantiableType(Type type, string reason)
    {
    }

    public virtual void StartType(Type type)
    {
    }

    public virtual void FinishType(Type type)
    {
    }

    public virtual void StartMethod(Type type, MethodBase method)
    {
    }

    public virtual bool FinishMethod(Type type, MethodBase method)
    {
        return true;
    }

    public virtual void UninstantiableMethod(MethodBase method)
    {

    }

    public virtual void UninstantiableMethods(MethodBase[] methods)
    {

    }

    public TimeSpan ElapsedTime()
    {
        return DateTime.Now - startTime;
    }
}

// Support for counting assemblies, types and methods.
class CounterBase : Visitor
{
    protected int typeCount;
    protected int uninstantiableTypeCount;
    protected int methodCount;
    protected int uninstantiableMethodCount;

    public int TypeCount => typeCount;
    public int UninstantiableTypeCount => uninstantiableTypeCount;
    public int MethodCount => methodCount;
    public int UninstantiableMethodCount => uninstantiableMethodCount;

    public override void StartAssembly(Assembly assembly)
    {
        base.StartAssembly(assembly);
        typeCount = 0;
        methodCount = 0;
        uninstantiableTypeCount = 0;
        uninstantiableMethodCount = 0;
    }

    public override void FinishType(Type type)
    {
        base.FinishType(type);
        typeCount++;
    }

    public override void UninstantiableType(Type type, string reason)
    {
        base.UninstantiableType(type, reason);
        uninstantiableTypeCount++;
    }

    public override bool FinishMethod(Type type, MethodBase method)
    {
        bool result = base.FinishMethod(type, method);
        methodCount++;
        return result;
    }

    public override void UninstantiableMethod(MethodBase method)
    {
        base.UninstantiableMethod(method);
        uninstantiableMethodCount++;
    }

    public override void UninstantiableMethods(MethodBase[] methods)
    {
        base.UninstantiableMethods(methods);
        uninstantiableMethodCount += methods.Length;
    }
}

// Counts types and methods
class Counter : CounterBase
{
    public override void StartAssembly(Assembly assembly)
    {
        base.StartAssembly(assembly);
        Console.WriteLine($"Computing Count for {assemblyName}");
    }

    public override void StartType(Type type)
    {
        base.StartType(type);
        Console.WriteLine($"#types: {typeCount}, #methods: {methodCount}, before type {type.FullName}");
    }

    public override void FinishAssembly(Assembly assembly)
    {
        base.FinishAssembly(assembly);
        TimeSpan elapsed = ElapsedTime();
        Console.WriteLine(
            $"Counts {assemblyName} - #types: {typeCount}, #methods: {methodCount}, " +
            $"skipped types: {uninstantiableTypeCount}, skipped methods: {uninstantiableMethodCount}, " +
            $"elapsed ms: {elapsed.TotalMilliseconds:F2}");
    }
}

// Invoke the jit on some methods
abstract class PrepareBase : CounterBase
{
    protected int firstMethod;
    protected int methodsPrepared;
    protected DateTime startType;
    protected bool _verbose;
    protected bool _time;

    public PrepareBase(int f, bool verbose, bool time)
    {
        firstMethod = f;
        _verbose = verbose;
        _time = time;
    }

    public override void StartAssembly(Assembly assembly)
    {
        base.StartAssembly(assembly);
        methodsPrepared = 0;
    }

    public override void FinishAssembly(Assembly assembly)
    {
        base.FinishAssembly(assembly);

        TimeSpan elapsed = ElapsedTime();
        Console.Write(
            $"Completed assembly {assemblyName} - #types: {typeCount}, #methods: {methodsPrepared}, " +
            $"skipped types: {uninstantiableTypeCount}, skipped methods: {uninstantiableMethodCount}");

        if (_time || _verbose)
        {
            Console.WriteLine($", time: {elapsed.TotalMilliseconds:F2}ms");
        }
        else
        {
            Console.WriteLine("");
        }
    }

    public override void StartType(Type type)
    {
        base.StartType(type);
        if (_verbose)
        {
            Console.WriteLine($"Start type {type.FullName}");
        }
        startType = DateTime.Now;
    }

    public override void FinishType(Type type)
    {
        if (_verbose)
        {
            TimeSpan elapsedType = DateTime.Now - startType;
            Console.WriteLine($"Completed type {type.FullName}, elapsed ms: {elapsedType.TotalMilliseconds:F2}");
        }
        base.FinishType(type);
    }

    public override void UninstantiableType(Type type, string reason)
    {
        if (_verbose)
        {
            Console.WriteLine($"Unable to instantiate {type.FullName}: {reason}");
        }
        base.UninstantiableType(type, reason);
    }

    public override void StartMethod(Type type, MethodBase method)
    {
        base.StartMethod(type, method);
        AttemptMethod(type, method);
    }

    public abstract void AttemptMethod(Type type, MethodBase method);

    protected TimeSpan PrepareMethod(Type type, MethodBase method)
    {
        TimeSpan elapsedFunc = TimeSpan.MinValue;

        try
        {
            DateTime startFunc = DateTime.Now;
            GC.WaitForPendingFinalizers();
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
            elapsedFunc = DateTime.Now - startFunc;
        }
        catch (System.EntryPointNotFoundException)
        {
            Console.WriteLine();
            Console.WriteLine($"EntryPointNotFoundException {type.FullName}::{method.Name}");
        }
        catch (System.BadImageFormatException)
        {
            Console.WriteLine();
            Console.WriteLine($"BadImageFormatException {type.FullName}::{method.Name}");
        }
        catch (System.MissingMethodException)
        {
            Console.WriteLine();
            Console.WriteLine($"MissingMethodException {type.FullName}::{method.Name}");
        }
        catch (System.ArgumentException e)
        {
            Console.WriteLine();
            string msg = e.Message.Split(new char[] { '\r', '\n' })[0];
            Console.WriteLine($"ArgumentException {type.FullName}::{method.Name} {msg}");
        }
        catch (System.IO.FileNotFoundException eFileNotFound)
        {
            Console.WriteLine();
            Console.WriteLine($"FileNotFoundException {type.FullName}::{method.Name}" +
                $" - {eFileNotFound.FileName} ({eFileNotFound.Message})");
        }
        catch (System.DllNotFoundException eDllNotFound)
        {
            Console.WriteLine();
            Console.WriteLine($"DllNotFoundException {type.FullName}::{method.Name} ({eDllNotFound.Message})");
        }
        catch (System.TypeInitializationException eTypeInitialization)
        {
            Console.WriteLine();
            Console.WriteLine("TypeInitializationException {type.FullName}::{method.Name}" +
                $"{eTypeInitialization.TypeName} ({eTypeInitialization.Message})");
        }
        catch (System.Runtime.InteropServices.MarshalDirectiveException)
        {
            Console.WriteLine();
            Console.WriteLine($"MarshalDirectiveException {type.FullName}::{method.Name}");
        }
        catch (System.TypeLoadException)
        {
            Console.WriteLine();
            Console.WriteLine($"TypeLoadException {type.FullName}::{method.Name}");
        }
        catch (System.OverflowException)
        {
            Console.WriteLine();
            Console.WriteLine($"OverflowException {type.FullName}::{method.Name}");
        }
        catch (System.InvalidProgramException)
        {
            Console.WriteLine();
            Console.WriteLine($"InvalidProgramException {type.FullName}::{method.Name}");
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Console.WriteLine($"Unknown exception {type.FullName}::{method.Name}");
            Console.WriteLine(e);
        }

        return elapsedFunc;
    }
}

// Invoke the jit on all methods starting from an initial method.
// By default the initial method is the first one visisted.
class PrepareAll : PrepareBase
{
    string pmiFullLogFileName;
    string pmiPartialLogFileName;
    string markerFileName;
    public PrepareAll(int firstMethod, bool verbose, bool time) : base(firstMethod, verbose, time)
    {
    }

    public override void StartAssembly(Assembly assembly)
    {
        base.StartAssembly(assembly);
        if (_verbose)
        {
            Console.WriteLine($"Prepall for {assemblyName}");
        }
        pmiFullLogFileName = $"{assemblyName}.pmi";
        pmiPartialLogFileName = $"{assemblyName}.pmiPartial";
        markerFileName = $"NextMethodToPrep-{assemblyName}.marker";
    }

    public override void AttemptMethod(Type type, MethodBase method)
    {
        // For now only emit marker files if not in verbose mode.
        //
        // When may PMIs are invoked by jit-diff / jit-dasm-pmi we
        // have trouble with file name contention as the names we
        // pick are not unique enough. But the names also need to be
        // predictable by DRIVEALL.
        //
        // So as a hack we rely on the fact that DRIVEALL always invokes
        // PMI in verbose mode, and jit-dasm-pmi in quiet mode.
        if (_verbose)
        {
            WriteAndFlushNextMethodToPrepMarker();
        }

        if (methodCount >= firstMethod)
        {
            methodsPrepared++;

            if (method.IsAbstract)
            {
                if (_verbose)
                {
                    Console.WriteLine($"PREPALL type# {typeCount} method# {methodCount} {type.FullName}::{method.Name} - skipping (abstract)");
                }
            }
            else if (method.ContainsGenericParameters)
            {
                if (_verbose)
                {
                    Console.WriteLine($"PREPALL type# {typeCount} method# {methodCount} {type.FullName}::{method.Name}  - skipping (generic parameters)");
                }
                UninstantiableMethod(method);
            }
            else
            {
                if (_verbose)
                {
                    Console.WriteLine($"PREPALL type# {typeCount} method# {methodCount} {type.FullName}::{method.Name}");
                }

                TimeSpan elapsedFunc = PrepareMethod(type, method);

                if (_verbose)
                {
                    Console.Write($"Completed method {type.FullName}::{method.Name}");
                    if (elapsedFunc != TimeSpan.MinValue)
                    {
                        Console.WriteLine($", elapsed ms: {elapsedFunc.TotalMilliseconds:F2}");
                    }
                    else
                    {
                        Console.WriteLine();
                    }
                }
            }
        }
    }

    public override void FinishAssembly(Assembly assembly)
    {
        base.FinishAssembly(assembly);
        if (File.Exists(markerFileName))
        {
            File.Delete(markerFileName);
        }
    }

    private void WriteAndFlushNextMethodToPrepMarker()
    {
        int nextMethodToPrep = (methodCount + 1);
        using (var writer = new StreamWriter(File.Create(markerFileName)))
        {
            writer.Write($"{nextMethodToPrep}");
        }
    }
}

// Invoke the jit on exactly one method.
class PrepareOne : PrepareBase
{
    public PrepareOne(int firstMethod, bool verbose, bool time) : base(firstMethod, verbose, time)
    {
    }

    public override void StartAssembly(Assembly assembly)
    {
        base.StartAssembly(assembly);
        Console.WriteLine($"Prepone for {assemblyName} method {firstMethod} ");
    }

    public override void AttemptMethod(Type type, MethodBase method)
    {
        if (methodCount >= firstMethod)
        {
            methodsPrepared++;

            if (method.IsAbstract)
            {
                Console.WriteLine($"PREPONE type# {typeCount} method# {methodCount} {type.FullName}::{method.Name} - skipping (abstract)");
            }
            else if (method.ContainsGenericParameters)
            {
                Console.WriteLine($"PREPONE type# {typeCount} method# {methodCount} {type.FullName}::{method.Name}  - skipping (generic parameters)");
            }
            else
            {
                Console.WriteLine($"PREPONE type# {typeCount} method# {methodCount} {type.FullName}::{method.Name}");
                TimeSpan elapsedFunc = PrepareMethod(type, method);
                Console.WriteLine($"Completed method {type.FullName}::{method.Name}");
                if (elapsedFunc != TimeSpan.MinValue)
                {
                    Console.WriteLine($", elapsed ms: {elapsedFunc.TotalMilliseconds:F2}");
                }
                else
                {
                    Console.WriteLine();
                }
            }
        }
    }

    public override bool FinishMethod(Type type, MethodBase method)
    {
        bool baseResult = base.FinishMethod(type, method);
        return baseResult && (methodCount <= firstMethod);
    }
}

static class GlobalMethodHolder
{
    public static MethodBase[] GlobalMethodInfoSet;

    public static void PopulateGlobalMethodInfoSet(MethodBase[] globalMethods)
    {
        GlobalMethodInfoSet = globalMethods;
    }
}

// The worker is responsible for driving the visitor through the
// types and methods of an assembly.
//
// It includes the generic instantiation strategy.
class Worker
{
    Visitor visitor;

    int goodAssemblyCount;
    int badAssemblyCount;
    int nonAssemblyCount;

    public Worker(Visitor v)
    {
        visitor = v;
        goodAssemblyCount = 0;
        badAssemblyCount = 0;
        nonAssemblyCount = 0;
    }

    private static BindingFlags BindingFlagsForCollectingAllMethodsOrCtors = (
        BindingFlags.DeclaredOnly |
        BindingFlags.Instance |
        BindingFlags.NonPublic |
        BindingFlags.Public |
        BindingFlags.Static
    );

    private Assembly LoadAssembly(string assemblyPath)
    {
        Assembly result = null;

        // The core library needs special handling as it often is in fragile ngen format
        if (assemblyPath.EndsWith("System.Private.CoreLib.dll") || assemblyPath.EndsWith("mscorlib.dll"))
        {
            result = typeof(object).Assembly;
        }
        else
        {
            try
            {
                result = Assembly.LoadFrom(assemblyPath);
                goodAssemblyCount++;
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"Assembly load failure ({assemblyPath}): ArgumentException");
                badAssemblyCount++;
            }
            catch (BadImageFormatException e)
            {
                Console.WriteLine($"Assembly load failure ({assemblyPath}): BadImageFormatException (is it a managed assembly?)");
                Console.WriteLine(e);
                nonAssemblyCount++;
            }
            catch (FileLoadException)
            {
                Console.WriteLine($"Assembly load failure ({assemblyPath}): FileLoadException");
                badAssemblyCount++;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Assembly load failure ({assemblyPath}): file not found");
                badAssemblyCount++;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"Assembly load failure ({assemblyPath}): UnauthorizedAccessException");
                badAssemblyCount++;
            }
        }

        return result;
    }

    static MethodBase[] GetMethods(Type t)
    {
        if (Object.ReferenceEquals(t, typeof(GlobalMethodHolder)))
        {
            return GlobalMethodHolder.GlobalMethodInfoSet;
        }

        MethodInfo[] mi = t.GetMethods(BindingFlagsForCollectingAllMethodsOrCtors);
        ConstructorInfo[] ci = t.GetConstructors(BindingFlagsForCollectingAllMethodsOrCtors);
        MethodBase[] mMI = new MethodBase[mi.Length + ci.Length];

        for (int i = 0; i < mi.Length; i++)
        {
            mMI[i] = mi[i];
        }

        for (int i = 0; i < ci.Length; i++)
        {
            mMI[i + mi.Length] = ci[i];
        }

        return mMI;
    }

    private static List<Type> LoadTypes(Assembly assembly)
    {
        List<Type> result = new List<Type>();

        var globalMethods = assembly.ManifestModule.GetMethods(BindingFlagsForCollectingAllMethodsOrCtors);

        if (globalMethods.Length > 0)
        {
            GlobalMethodHolder.PopulateGlobalMethodInfoSet(globalMethods);
            result.Add(typeof(GlobalMethodHolder));
        }

        string assemblyName = assembly.GetName().Name;

        try
        {
            result.AddRange(assembly.GetTypes());
            return result;
        }
        catch (ReflectionTypeLoadException e)
        {
            Console.WriteLine($"ReflectionTypeLoadException {assemblyName}");
            Exception[] ea = e.LoaderExceptions;
            foreach (Exception e2 in ea)
            {
                Console.WriteLine($"ReflectionTypeLoadException {assemblyName}   ex: {e2.Message}");
            }

            if (e.Types != null)
            {
                foreach (Type t in e.Types)
                {
                    if (t != null)
                    {
                        Console.WriteLine($"ReflectionTypeLoadException {assemblyName} type: {t.Name}");
                    }
                }
            }
            return null;
        }
        catch (FileLoadException)
        {
            Console.WriteLine($"FileLoadException {assemblyName}");
            return null;
        }
        catch (FileNotFoundException e)
        {
            string temp = e.ToString();
            string[] ts = temp.Split('\'');
            temp = ts[1];
            Console.WriteLine($"FileNotFoundException {assemblyName} : {temp}");
            return null;
        }
    }

    public int Work(IEnumerable<string> assemblyNames)
    {
        int maxResult = 0;
        int goodTypeCount = 0;
        int badTypeCount = 0;
        int goodMethodCount = 0;
        int badMethodCount = 0;
        DateTime startTime = DateTime.Now;

        visitor.Start();

        foreach (string assemblyName in assemblyNames)
        {
            int thisResult = Work(assemblyName);

            if ((thisResult == 0) && visitor is CounterBase)
            {
                CounterBase counterVisitor = visitor as CounterBase;
                goodTypeCount += counterVisitor.TypeCount;
                goodMethodCount += counterVisitor.MethodCount;
                badTypeCount += counterVisitor.UninstantiableTypeCount;
                badMethodCount += counterVisitor.UninstantiableMethodCount;
            }

            maxResult = Math.Max(thisResult, maxResult);
        }

        visitor.Finish();

        // Produce summary if visitor's final output is not sufficient.
        if ((badAssemblyCount > 0) || (nonAssemblyCount > 0) || (goodAssemblyCount > 1))
        {
            DateTime stopTime = DateTime.Now;
            TimeSpan totalTime = stopTime - startTime;
            Console.WriteLine();
            Console.WriteLine($"Overall: {goodAssemblyCount} assemblies {goodTypeCount} types {goodMethodCount} methods in {totalTime.TotalMilliseconds:F2}ms");
            Console.WriteLine($"         {nonAssemblyCount} non-assemblies {badAssemblyCount} skipped assemblies {badTypeCount} skipped types {badMethodCount} skipped methods");
        }

        return maxResult;
    }

    public int Work(string assemblyName)
    {
        Assembly assembly = LoadAssembly(Path.GetFullPath(assemblyName));

        if (assembly == null)
        {
            return 102;
        }

        List<Type> types = LoadTypes(assembly);

        if (types == null)
        {
            return 103;
        }

        visitor.StartAssembly(assembly);

        bool keepGoing = true;

        foreach (Type t in types)
        {
            // Skip types with no jittable methods
            if (t.IsInterface)
            {
                continue;
            }

            // Likewise there are no methods of interest in delegates.
            if (t.IsSubclassOf(typeof(System.Delegate)))
            {
                continue;
            }

            if (t.IsGenericType)
            {
                List<Type> instances = GetInstances(t);

                foreach (Type ti in instances)
                {
                    keepGoing = Work(ti);
                    if (!keepGoing)
                    {
                        break;
                    }
                }
            }
            else
            {
                keepGoing = Work(t);
            }

            if (!keepGoing)
            {
                break;
            }
        }

        visitor.FinishAssembly(assembly);

        return 0;
    }

    bool Work(Type type)
    {
        visitor.StartType(type);
        bool keepGoing = true;
        foreach (MethodBase methodBase in GetMethods(type))
        {
            if (methodBase.IsGenericMethod)
            {
                List<MethodBase> instanceMethods = GetInstances(type, methodBase);

                foreach(MethodBase instanceMethod in instanceMethods)
                {
                    visitor.StartMethod(type, instanceMethod);
                    keepGoing = visitor.FinishMethod(type, instanceMethod);
                    if (!keepGoing)
                    {
                        break;
                    }
                }
            }
            else
            {
                visitor.StartMethod(type, methodBase);
                keepGoing = visitor.FinishMethod(type, methodBase);
                if (!keepGoing)
                {
                    break;
                }
            }
        }

        visitor.FinishType(type);

        return keepGoing;
    }

    private List<Type> GetInstances(Type type)
    {
        List<Type> results = new List<Type>();

        // Get the args for this generic
        Type[] genericArguments = type.GetGenericArguments();

        // Only handle the very simplest cases for now
        if (genericArguments.Length > 2)
        {
            visitor.UninstantiableType(type, "too many type parameters");
            MethodBase[] methods = GetMethods(type);
            visitor.UninstantiableMethods(methods);
            return results;
        }

        // Types we will use for instantiation attempts.
        Type[] typesToTry = new Type[] { typeof(object), typeof(int), typeof(double), typeof(Vector<float>), typeof(long) };

        // To keep things sane, we won't try and instantiate too many copies
        int instantiationLimit = genericArguments.Length * typesToTry.Length;
        int instantiationCount = 0;

        foreach (Type firstType in typesToTry)
        {
            if (!AreConstraintsSatisfied(firstType, genericArguments[0]))
            {
                continue;
            }

            Type newType = null;

            if (genericArguments.Length == 1)
            {
                try
                {
                    newType = type.MakeGenericType(firstType);
                }
                catch (Exception)
                {

                }
            }
            else if (genericArguments.Length == 2)
            {
                foreach (Type secondType in typesToTry)
                {
                    if (!AreConstraintsSatisfied(secondType, genericArguments[1]))
                    {
                        continue;
                    }

                    try
                    {
                        newType = type.MakeGenericType(firstType, secondType);
                    }
                    catch (Exception)
                    {

                    }
                }
            }

            // If we can instantiate, prepare the methods.
            if (newType != null)
            {
                instantiationCount++;
                results.Add(newType);
            }

            if (instantiationCount >= instantiationLimit)
            {
                break;
            }
        }

        if (instantiationCount == 0)
        {
            visitor.UninstantiableType(type, "could not find valid substitution");
            MethodBase[] methods = GetMethods(type);
            visitor.UninstantiableMethods(methods);
        }

        return results;
    }

    private List<MethodBase> GetInstances(Type type, MethodBase method)
    {
        List<MethodBase> results = new List<MethodBase>();

        // Get the args for this generic
        Type[] genericArguments = method.GetGenericArguments();

        // Only handle the very simplest cases for now
        if (genericArguments.Length > 2)
        {
            visitor.UninstantiableMethod(method);
            return results;
        }

        MethodInfo methodInfo = method as MethodInfo;

        if (methodInfo == null)
        {
            visitor.UninstantiableMethod(method);
            return results;
        }

        // Types we will use for instantiation attempts.
        Type[] typesToTry = new Type[] { typeof(object), typeof(int), typeof(double), typeof(Vector<float>), typeof(long) };

        // To keep things sane, we won't try and instantiate too many copies
        int instantiationLimit = genericArguments.Length * typesToTry.Length;
        int instantiationCount = 0;

        foreach (Type firstType in typesToTry)
        {
            if (!AreConstraintsSatisfied(firstType, genericArguments[0]))
            {
                continue;
            }

            MethodBase newMethod = null;

            if (genericArguments.Length == 1)
            {
                try
                {
                    newMethod = methodInfo.MakeGenericMethod(firstType);
                }
                catch (Exception)
                {

                }
            }
            else if (genericArguments.Length == 2)
            {
                foreach (Type secondType in typesToTry)
                {
                    if (!AreConstraintsSatisfied(secondType, genericArguments[1]))
                    {
                        continue;
                    }
                    try
                    {
                        newMethod = methodInfo.MakeGenericMethod(firstType, secondType);
                    }
                    catch (Exception)
                    {

                    }
                }
            }

            // If we can instantiate, prepare the methods.
            if (newMethod != null)
            {
                instantiationCount++;
                results.Add(newMethod);
            }
        }

        if (instantiationCount == 0)
        {
            visitor.UninstantiableMethod(method);
        }

        return results;
    }

    // Try and identify obviously invalid type substitutions.
    //
    // It is ok if we miss some, as we catch the exception that will
    // arise when instantating with an invalid type.
    static bool AreConstraintsSatisfied(Type type, Type parameterType)
    {
        bool areConstraintsSatisfied = true;

        // Check special constraints
        GenericParameterAttributes gpa = parameterType.GenericParameterAttributes;

        if ((gpa & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
        {
            if (type.IsValueType)
            {
                areConstraintsSatisfied = false;
            }
        }
        else if ((gpa & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
        {
            if (!type.IsValueType)
            {
                areConstraintsSatisfied = false;
            }
        }
        else if ((gpa & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
        {
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                areConstraintsSatisfied = false;
            }
        }

        // If all special constaints are satisfied, check type constraints
        if (areConstraintsSatisfied)
        {
            Type[] constraints = parameterType.GetGenericParameterConstraints();

            foreach (Type c in constraints)
            {
                // If the constraint is also generic, just bail on checks.
                // The runtime checks will determine if this constraint is satisfied.
                if (c.ContainsGenericParameters)
                {
                    continue;
                }

                if (!c.IsAssignableFrom(type))
                {
                    areConstraintsSatisfied = false;
                    break;
                }
            }
        }

        return areConstraintsSatisfied;
    }
}

class PrepareMethodinator
{
    private static Assembly ResolveEventHandler(object sender, ResolveEventArgs args)
    {
        string pmiPath = Environment.GetEnvironmentVariable("PMIPATH");
        if (pmiPath == null)
        {
            return null;
        }

        string[] pmiPaths = pmiPath.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string path in pmiPaths)
        {
            // what is the format of this?
            int idx = args.Name.IndexOf(",");
            if (idx != -1)
            {
                string tmpPath = Path.GetFullPath(Path.Combine(path, args.Name.Substring(0, idx) + ".dll"));
                if (File.Exists(tmpPath))
                {
                    // Found it!
                    try
                    {
                        return Assembly.LoadFrom(tmpPath);
                    }
                    catch (Exception)
                    {
                        // Well, that didn't work!
                    }
                }
            }
        }

        return null;
    }

    private static int Usage()
    {
        string exeName = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; // get the current full path name of PMI.exe
        exeName = System.IO.Path.GetFileName(exeName); // strip off the path; just use the EXE name.
        // Augment if we're using core executables that require an explicit host.
        // (note user may have actually launched via pmi.bat -- not clear how to tell that just yet).
        if (exeName.IndexOf("dotnet") == 0 || exeName.IndexOf("corerun") == 0)
        {
            exeName += " pmi.dll";
        }

        Console.WriteLine(
            "Usage:\r\n"
            + "\r\n"
            + "  " + exeName + " Count PATH_TO_ASSEMBLY\r\n"
            + "      Count the number of types and methods in an assembly.\r\n"
            + "\r\n"
            + "  " + exeName + " PrepOne PATH_TO_ASSEMBLY INDEX_OF_TARGET_METHOD\r\n"
            + "      JIT a single method, specified by a method number.\r\n"
            + "\r\n"
            + "  " + exeName + " PrepAll PATH_TO_ASSEMBLY [INDEX_OF_FIRST_METHOD_TO_PROCESS]\r\n"
            + "      JIT all the methods in an assembly. If INDEX_OF_FIRST_METHOD_TO_PROCESS is specified, it is the first\r\n"
            + "      method compiled, followed by all subsequent methods.\r\n"
            + "\r\n"
            + "  " + exeName + " DriveAll PATH_TO_ASSEMBLY\r\n"
            + "      The same as PrepAll, but is more robust. While PrepAll will stop at the first JIT assert, DriveAll will\r\n"
            + "      continue by skipping that method.\r\n"
            + "\r\n"
            + "Environment variable PMIPATH is a semicolon-separated list of paths used to find dependent assemblies.\r\n"
            + "\r\n"
            + "For Prepall and Prepone, optional suffixes will change output behavior:\r\n"
            + "   -Quiet will suppress in-progress messages for type and method exploration\r\n"
            + "   -Time will always show elapsed times, even in -Quiet mode"
        );

        return 101;
    }

    // Return values:
    // 0 - success
    // >= 100 - failure
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            return Usage();
        }

        string command = args[0].ToUpper();
        string assemblyName = args[1];
        int methodToPrep = -1; // For PREPONE, PREPALL. For PREPALL, this is the first method to prep.

        Visitor v = null;

        int dashIndex = command.IndexOf('-');
        string rootCommand = dashIndex < 0 ? command : command.Substring(0, dashIndex);
        switch (rootCommand)
        {
            case "DRIVEALL":
            case "COUNT":
                if (args.Length < 2)
                {
                    Console.WriteLine("ERROR: too few arguments");
                    return Usage();
                }
                else if (args.Length > 2)
                {
                    Console.WriteLine("ERROR: too many arguments");
                    return Usage();
                }

                if (command == "DRIVEALL")
                {
                    return PMIDriver.PMIDriver.Drive(assemblyName);
                }

                v = new Counter();
                break;

            case "PREPALL":
            case "PREPONE":
                if (args.Length < 3)
                {
                    methodToPrep = 0;
                }
                else if (args.Length > 3)
                {
                    Console.WriteLine("ERROR: too many arguments");
                    return Usage();
                }
                else
                {
                    try
                    {
                        methodToPrep = Convert.ToInt32(args[2]);
                    }
                    catch (System.FormatException)
                    {
                        Console.WriteLine("ERROR: illegal method number");
                        return Usage();
                    }
                }

                bool all = command.IndexOf("ALL") > 0;
                bool verbose = !(command.IndexOf("QUIET") > 0);
                bool time = verbose || command.IndexOf("TIME") > 0;

                if (all)
                {
                    v = new PrepareAll(methodToPrep, verbose, time);
                }
                else
                {
                    v = new PrepareOne(methodToPrep, verbose, time);
                }
                break;

            default:
                Console.WriteLine("ERROR: Unknown command {0}", command);
                return Usage();
        }

        // We want to handle specifying a "load path" where assemblies can be found.
        // The environment variable PMIPATH is a semicolon-separated list of paths. If the
        // Assembly can't be found by the usual mechanisms, our Assembly ResolveEventHandler
        // will be called, and we'll probe on the PMIPATH list.
        AppDomain currentDomain = AppDomain.CurrentDomain;
        currentDomain.AssemblyResolve += new ResolveEventHandler(ResolveEventHandler);

        Worker w = new Worker(v);
        int result = 0;
        string msg = "a file";

#if NETCOREAPP2_1
        msg += " or a directory";
        if (Directory.Exists(assemblyName))
        {
            EnumerationOptions options = new EnumerationOptions();
            options.RecurseSubdirectories = true;
            IEnumerable<string> exeFiles = Directory.EnumerateFiles(assemblyName, "*.exe", options);
            IEnumerable<string> dllFiles = Directory.EnumerateFiles(assemblyName, "*.dll", options);
            IEnumerable<string> allFiles = exeFiles.Concat(dllFiles);
            result = w.Work(allFiles);
        }
        else
#endif

        if (File.Exists(assemblyName))
        {
            result = w.Work(assemblyName);
        }
        else
        {
            Console.WriteLine($"ERROR: {assemblyName} is not {msg}");
            result = 101;
        }

        return result;
    }
}
