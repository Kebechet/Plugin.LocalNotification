﻿using Foundation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using UIKit;
using UserNotifications;

namespace Plugin.LocalNotification.Platform.iOS
{
    /// <inheritdoc />
    public class NotificationServiceImpl : INotificationService
    {

        // All identifiers must be unique
        public Dictionary<string, NotificationAction> NotificationActions { get; } = new Dictionary<string, NotificationAction>();

        /// <inheritdoc />
        public event NotificationTappedEventHandler NotificationTapped;

        /// <inheritdoc />
        public event NotificationReceivedEventHandler NotificationReceived;

        /// <inheritdoc />
        public void OnNotificationTapped(NotificationTappedEventArgs e)
        {
            NotificationTapped?.Invoke(e);
        }

        /// <inheritdoc />
        public void OnNotificationReceived(NotificationReceivedEventArgs e)
        {
            NotificationReceived?.Invoke(e);
        }

        /// <inheritdoc />
        public bool Cancel(int notificationId)
        {
            try
            {
                if (UIDevice.CurrentDevice.CheckSystemVersion(10, 0) == false)
                {
                    return false;
                }

                var itemList = new[]
                {
                    notificationId.ToString(CultureInfo.CurrentCulture)
                };

                UNUserNotificationCenter.Current.RemovePendingNotificationRequests(itemList);
                UNUserNotificationCenter.Current.RemoveDeliveredNotifications(itemList);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        /// <inheritdoc />
        public bool CancelAll()
        {
            try
            {
                if (UIDevice.CurrentDevice.CheckSystemVersion(10, 0) == false)
                {
                    return false;
                }

                UNUserNotificationCenter.Current.RemoveAllPendingNotificationRequests();
                UNUserNotificationCenter.Current.RemoveAllDeliveredNotifications();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }

        /// <inheritdoc />
        public Task<bool> Show(Func<NotificationRequestBuilder, NotificationRequest> builder) => Show(builder.Invoke(new NotificationRequestBuilder()));

        /// <inheritdoc />
        public async Task<bool> Show(NotificationRequest notificationRequest)
        {
            UNNotificationTrigger trigger = null;
            try
            {
                if (UIDevice.CurrentDevice.CheckSystemVersion(10, 0) == false)
                {
                    return false;
                }

                if (notificationRequest is null)
                {
                    return false;
                }

                var allowed = await NotificationCenter.AskPermissionAsync().ConfigureAwait(false);
                if (allowed == false)
                {
                    return false;
                }

                var userInfoDictionary = new NSMutableDictionary();
                var dictionary = NotificationCenter.GetRequestSerialize(notificationRequest);
                foreach (var item in dictionary)
                {
                    userInfoDictionary.SetValueForKey(new NSString(item.Value), new NSString(item.Key));
                }

                using var content = new UNMutableNotificationContent
                {
                    Title = notificationRequest.Title,
                    Subtitle = notificationRequest.Subtitle,
                    Body = notificationRequest.Description,
                    Badge = notificationRequest.BadgeNumber,
                    UserInfo = userInfoDictionary,
                    Sound = UNNotificationSound.Default,
                    CategoryIdentifier = ToNativeCategory(notificationRequest.Category),
                };
                if (string.IsNullOrWhiteSpace(notificationRequest.Sound) == false)
                {
                    content.Sound = UNNotificationSound.GetSound(notificationRequest.Sound);
                }

                var repeats = notificationRequest.Schedule.RepeatType != NotificationRepeat.No;

                if (repeats && notificationRequest.Schedule.RepeatType == NotificationRepeat.TimeInterval &&
                    notificationRequest.Schedule.NotifyRepeatInterval.HasValue)
                {
                    TimeSpan interval = notificationRequest.Schedule.NotifyRepeatInterval.Value;

                    // Cannot delay and repeat in when TimeInterval
                    trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(interval.TotalSeconds, true);
                }
                else
                {
                    using var notifyTime = GetNsDateComponentsFromDateTime(notificationRequest);
                    trigger = UNCalendarNotificationTrigger.CreateTrigger(notifyTime, repeats);
                }

                var notificationId =
                    notificationRequest.NotificationId.ToString(CultureInfo.CurrentCulture);

                var request = UNNotificationRequest.FromIdentifier(notificationId, content, trigger);

                await UNUserNotificationCenter.Current.AddNotificationRequestAsync(request)
                    .ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
            finally
            {
                trigger?.Dispose();
            }
        }

        private static NSDateComponents GetNsDateComponentsFromDateTime(NotificationRequest notificationRequest)
        {
            var dateTime = notificationRequest.Schedule.NotifyTime ?? DateTime.Now.AddSeconds(1);

            return notificationRequest.Schedule.RepeatType switch
            {
                NotificationRepeat.Daily => new NSDateComponents
                {
                    Hour = dateTime.Hour,
                    Minute = dateTime.Minute,
                    Second = dateTime.Second
                },
                NotificationRepeat.Weekly => new NSDateComponents
                {
                    // iOS: Weekday units are the numbers 1 through n, where n is the number of days in the week.
                    // For example, in the Gregorian calendar, n is 7 and Sunday is represented by 1.
                    // .Net: The returned value is an integer between 0 and 6,
                    // where 0 indicates Sunday, 1 indicates Monday, 2 indicates Tuesday, 3 indicates Wednesday, 4 indicates Thursday, 5 indicates Friday, and 6 indicates Saturday.
                    Weekday = (int)dateTime.DayOfWeek + 1,
                    Hour = dateTime.Hour,
                    Minute = dateTime.Minute,
                    Second = dateTime.Second
                },
                NotificationRepeat.No => new NSDateComponents
                {
                    Day = dateTime.Day,
                    Month = dateTime.Month,
                    Year = dateTime.Year,
                    Hour = dateTime.Hour,
                    Minute = dateTime.Minute,
                    Second = dateTime.Second
                },
                _ => new NSDateComponents
                {
                    Day = dateTime.Day,
                    Hour = dateTime.Hour,
                    Minute = dateTime.Minute,
                    Second = dateTime.Second
                }
            };
        }

        /// <inheritdoc />
        public void RegisterCategories(NotificationCategory[] notificationCategories)
        {
            var categories = new List<UNNotificationCategory>();

            foreach (var category in notificationCategories)
            {
                var notificationCategory = RegisterActions(category);

                categories.Add(notificationCategory);
            }

            UNUserNotificationCenter.Current.SetNotificationCategories(new NSSet<UNNotificationCategory>(categories.ToArray()));
        }

        private static UNNotificationCategory RegisterActions(NotificationCategory category)
        {
            foreach (var notificationAction in category.NotificationActions)
            {
                NotificationCenter.Current.NotificationActions.Add(notificationAction.Identifier, notificationAction);
            }

            var notificationActions = category
                .NotificationActions
                .Select(t => UNNotificationAction.FromIdentifier(t.Identifier, t.Title, ToNativeActionType(t.ActionType)));

            var notificationCategory = UNNotificationCategory
                .FromIdentifier(ToNativeCategory(category.Type), notificationActions.ToArray(), Array.Empty<string>(), UNNotificationCategoryOptions.CustomDismissAction);

            return notificationCategory;
        }

        private static UNNotificationActionOptions ToNativeActionType(ActionTypes actionsType)
        {
            switch (actionsType)
            {
                case ActionTypes.Foreground:
                    return UNNotificationActionOptions.Foreground;

                case ActionTypes.Destructive:
                    return UNNotificationActionOptions.Destructive;

                case ActionTypes.AuthenticationRequired:
                    return UNNotificationActionOptions.AuthenticationRequired;

                default:
                    return UNNotificationActionOptions.None;
            }
        }

        private static string ToNativeCategory(NotificationCategoryTypes type)
        {
            switch (type)
            {
                case NotificationCategoryTypes.Alarm:
                    return "ALARM";

                default:
                    return string.Empty;
            }
        }
    }
}