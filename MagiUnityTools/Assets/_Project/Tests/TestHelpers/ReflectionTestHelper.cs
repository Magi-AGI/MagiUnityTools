using System;
using System.Reflection;

namespace Magi.UnityTools.TestHelpers
{
    /// <summary>
    /// Reflection utilities for accessing non-public members in tests.
    /// Extracts the pattern used across existing tests (ITUMSEventLoggerTests, GrowthMappingTests, etc.)
    /// </summary>
    public static class ReflectionTestHelper
    {
        private const BindingFlags NonPublicInstance =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private const BindingFlags NonPublicStatic =
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>
        /// Sets a non-public instance field on the target object.
        /// </summary>
        public static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, NonPublicInstance);
            if (field == null)
                throw new MissingFieldException(target.GetType().Name, fieldName);
            field.SetValue(target, value);
        }

        /// <summary>
        /// Gets a non-public instance field value from the target object.
        /// </summary>
        public static T GetField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, NonPublicInstance);
            if (field == null)
                throw new MissingFieldException(target.GetType().Name, fieldName);
            return (T)field.GetValue(target);
        }

        /// <summary>
        /// Invokes a non-public instance method on the target object.
        /// </summary>
        public static object InvokeMethod(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, NonPublicInstance);
            if (method == null)
                throw new MissingMethodException(target.GetType().Name, methodName);
            return method.Invoke(target, args);
        }

        /// <summary>
        /// Gets a non-public static property value.
        /// </summary>
        public static T GetStaticProperty<T>(Type type, string propertyName)
        {
            var prop = type.GetProperty(propertyName, NonPublicStatic);
            if (prop == null)
                throw new MissingMemberException(type.Name, propertyName);
            return (T)prop.GetValue(null);
        }

        /// <summary>
        /// Sets a non-public static property value.
        /// </summary>
        public static void SetStaticProperty(Type type, string propertyName, object value)
        {
            var prop = type.GetProperty(propertyName, NonPublicStatic);
            if (prop == null)
                throw new MissingMemberException(type.Name, propertyName);
            prop.SetValue(null, value);
        }
    }
}
